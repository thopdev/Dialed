using System.Net;
using Dialed.Api.Data;
using Microsoft.Azure.Cosmos;

namespace Dialed.Api.Households;

/// <summary>
/// Households and their invites in Cosmos DB (#38). The <c>households</c> container is partitioned by <c>/householdId</c>, like the
/// synced records: a household is the document <c>household</c> in its own partition, and each invite is a document <c>invite-CODE</c>
/// next to it, so reading a household, listing or revoking its invites and deleting it all stay in one partition. Only looking up a code
/// (joining) reads across partitions, by document id. Invites carry a TTL, so Cosmos DB removes them after they expire; reads still check the
/// expiry against the injected clock. Using an invite is an ETag write, so two people cannot both take its last use.
/// Memberships are not here: they live on the person (<c>IUserDirectory</c>).
/// </summary>
public sealed class CosmosHouseholdStore(DialedCosmos cosmos, TimeProvider time, IEnumerable<Household>? initial = null) : IHouseholdStore
{
    public static readonly CosmosContainerSpec Container = new("households", "/householdId");

    private const string HouseholdDocumentId = "household";
    private const string InvitePrefix = "invite-";
    private const int MaxRetries = 5;
    private static readonly TimeSpan TtlMargin = TimeSpan.FromMinutes(1);

    private readonly IReadOnlyList<Household> seed = [.. initial ?? []];
    private Task? seeded;

    public async Task<Household?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        var container = await ContainerAsync(cancellationToken);
        return (await ReadAsync<HouseholdDocument>(container, id, HouseholdDocumentId, cancellationToken)).Document?.ToHousehold();
    }

    public async Task<Household> EnsureAsync(string id, string name, string ownerId, CancellationToken cancellationToken = default)
    {
        var container = await ContainerAsync(cancellationToken);
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            if ((await ReadAsync<HouseholdDocument>(container, id, HouseholdDocumentId, cancellationToken)).Document is { } existing)
            {
                return existing.ToHousehold();
            }

            var created = new Household(id, name, ownerId);
            try
            {
                await container.CreateItemAsync(HouseholdDocument.From(created), new PartitionKey(id), cancellationToken: cancellationToken);
                return created;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                // Created at the same moment elsewhere: read that one.
            }
        }

        throw new InvalidOperationException("Could not create the household.");
    }

    public async Task SaveAsync(Household household, CancellationToken cancellationToken = default)
    {
        var container = await ContainerAsync(cancellationToken);
        await container.UpsertItemAsync(HouseholdDocument.From(household), new PartitionKey(household.Id), cancellationToken: cancellationToken);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var container = await ContainerAsync(cancellationToken);
        var ids = await QueryAsync<IdOnly>(container, id, new QueryDefinition("SELECT c.id FROM c"), cancellationToken);
        foreach (var document in ids)
        {
            await DeleteIfThereAsync(container, id, document.Id, null, cancellationToken);
        }
    }

    public async Task AddInviteAsync(Invite invite, CancellationToken cancellationToken = default)
    {
        var container = await ContainerAsync(cancellationToken);
        await container.UpsertItemAsync(InviteDocument.From(invite, Ttl(invite)), new PartitionKey(invite.HouseholdId), cancellationToken: cancellationToken);
    }

    public async Task<Invite?> FindInviteAsync(string code, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var container = await ContainerAsync(cancellationToken);
        return (await FindInviteDocumentAsync(container, code, now, cancellationToken)).Document?.ToInvite();
    }

    public async Task<Invite?> TakeInviteAsync(string code, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var container = await ContainerAsync(cancellationToken);
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var (document, etag) = await FindInviteDocumentAsync(container, code, now, cancellationToken);
            if (document is null)
            {
                return null;
            }

            var used = document.ToInvite() with { Uses = document.Uses + 1 };
            var key = new PartitionKey(document.HouseholdId);
            var options = new ItemRequestOptions { IfMatchEtag = etag };
            try
            {
                if (used.Uses >= used.MaxUses)
                {
                    await container.DeleteItemAsync<InviteDocument>(document.Id, key, options, cancellationToken);
                }
                else
                {
                    await container.ReplaceItemAsync(InviteDocument.From(used, Ttl(used)), document.Id, key, options, cancellationToken);
                }

                return used;
            }
            catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
            {
                // Someone else used it at the same moment: look again.
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<Invite>> ListInvitesAsync(string householdId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var container = await ContainerAsync(cancellationToken);
        var query = new QueryDefinition("SELECT * FROM c WHERE c.type = 'invite'");
        var invites = await QueryAsync<InviteDocument>(container, householdId, query, cancellationToken);
        return [.. invites.Select(i => i.ToInvite()).Where(i => i.ExpiresAt > now).OrderBy(i => i.ExpiresAt).ThenBy(i => i.Code, StringComparer.Ordinal)];
    }

    public async Task<bool> RevokeInviteAsync(string householdId, string code, CancellationToken cancellationToken = default)
    {
        var container = await ContainerAsync(cancellationToken);
        return await DeleteIfThereAsync(container, householdId, InvitePrefix + code, null, cancellationToken);
    }

    /// <summary>The container, with the configured households (the Development ones) created once per instance.</summary>
    private async Task<Container> ContainerAsync(CancellationToken cancellationToken)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        if (seed.Count > 0)
        {
            var task = seeded ??= SeedAsync(container);
            try
            {
                await task.WaitAsync(cancellationToken);
            }
            catch (Exception) when (task.IsFaulted)
            {
                seeded = null;
                throw;
            }
        }

        return container;
    }

    private async Task SeedAsync(Container container)
    {
        foreach (var household in seed)
        {
            try
            {
                await container.CreateItemAsync(HouseholdDocument.From(household), new PartitionKey(household.Id));
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                // Already there (and maybe renamed): keep it.
            }
        }
    }

    private int Ttl(Invite invite) =>
        (int)Math.Ceiling(Math.Max(1, (invite.ExpiresAt - time.GetUtcNow() + TtlMargin).TotalSeconds));

    private static async Task<(InviteDocument? Document, string? ETag)> FindInviteDocumentAsync(Container container, string code, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // A code does not say which household it is for, so this is the one read across partitions (an id lookup, on joining only).
        var query = new QueryDefinition("SELECT c.householdId FROM c WHERE c.id = @id").WithParameter("@id", InvitePrefix + code);
        var hits = await QueryAsync<HouseholdIdOnly>(container, null, query, cancellationToken);
        if (hits.Count == 0)
        {
            return (null, null);
        }

        var (document, etag) = await ReadAsync<InviteDocument>(container, hits[0].HouseholdId, InvitePrefix + code, cancellationToken);
        return document is null || document.ExpiresAt <= now || document.Uses >= document.MaxUses ? (null, null) : (document, etag);
    }

    private static async Task<List<T>> QueryAsync<T>(Container container, string? householdId, QueryDefinition query, CancellationToken cancellationToken)
    {
        var options = householdId is null ? new QueryRequestOptions() : new QueryRequestOptions { PartitionKey = new PartitionKey(householdId) };
        using var iterator = container.GetItemQueryIterator<T>(query, requestOptions: options);
        var results = new List<T>();
        while (iterator.HasMoreResults)
        {
            results.AddRange(await iterator.ReadNextAsync(cancellationToken));
        }

        return results;
    }

    private static async Task<(T? Document, string? ETag)> ReadAsync<T>(Container container, string householdId, string id, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            var response = await container.ReadItemAsync<T>(id, new PartitionKey(householdId), cancellationToken: cancellationToken);
            return (response.Resource, response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, null);
        }
    }

    private static async Task<bool> DeleteIfThereAsync(Container container, string householdId, string id, string? etag, CancellationToken cancellationToken)
    {
        try
        {
            await container.DeleteItemAsync<IdOnly>(id, new PartitionKey(householdId), new ItemRequestOptions { IfMatchEtag = etag }, cancellationToken);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    internal sealed record HouseholdDocument
    {
        public string Id { get; init; } = HouseholdDocumentId;
        public string HouseholdId { get; init; } = "";
        public string Type { get; init; } = "household";
        public string Name { get; init; } = "";
        public string OwnerId { get; init; } = "";

        public static HouseholdDocument From(Household household) => new() { HouseholdId = household.Id, Name = household.Name, OwnerId = household.OwnerId };

        public Household ToHousehold() => new(HouseholdId, Name, OwnerId);
    }

    internal sealed record InviteDocument
    {
        public string Id { get; init; } = "";
        public string HouseholdId { get; init; } = "";
        public string Type { get; init; } = "invite";
        public string Code { get; init; } = "";
        public DateTimeOffset ExpiresAt { get; init; }
        public int MaxUses { get; init; } = 1;
        public int Uses { get; init; }

        /// <summary>Seconds until Cosmos DB removes the invite: until it expires, plus a margin.</summary>
        public int Ttl { get; init; }

        public static InviteDocument From(Invite invite, int ttl) => new()
        {
            Id = InvitePrefix + invite.Code,
            HouseholdId = invite.HouseholdId,
            Code = invite.Code,
            ExpiresAt = invite.ExpiresAt,
            MaxUses = invite.MaxUses,
            Uses = invite.Uses,
            Ttl = ttl,
        };

        public Invite ToInvite() => new(Code, HouseholdId, ExpiresAt, MaxUses, Uses);
    }

    private sealed record IdOnly(string Id);

    private sealed record HouseholdIdOnly(string HouseholdId);
}
