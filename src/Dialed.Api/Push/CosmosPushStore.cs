using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dialed.Api.Data;
using Dialed.Domain.Inventory;
using Microsoft.Azure.Cosmos;

namespace Dialed.Api.Push;

/// <summary>
/// Push subscriptions, switches, stock alert memory and the "already pushed" markers in Cosmos DB (#260), so a restart or a second
/// instance neither forgets devices nor sends an alert twice. The <c>push</c> container is partitioned by <c>/userId</c>: one document per
/// device (<c>sub-…</c>, the id is a hash of the endpoint), one <c>settings</c>, one <c>alerts</c> and short-lived <c>claim-…</c> markers
/// that TTL removes after the hour they guard. Devices have no TTL: the app only subscribes from a tap, so an expiry would silently stop
/// working devices; a device the push service rejects (404/410) is deleted by the sender instead.
/// </summary>
public sealed class CosmosPushStore(DialedCosmos cosmos) : IPushStore
{
    public static readonly CosmosContainerSpec Container = new("push", "/userId");

    private const string SubscriptionType = "subscription";
    private const string SettingsId = "settings";
    private const string AlertsId = "alerts";
    private const int MaxRetries = 5;

    /// <summary>Extra time a claim is kept after its window, so TTL never removes it early.</summary>
    private static readonly TimeSpan TtlMargin = TimeSpan.FromMinutes(1);

    public async Task SaveAsync(PushSubscription subscription, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        var hash = Hash(subscription.Endpoint);

        // An endpoint belongs to one person at a time: someone else signing in on the device moves it (a rare cross-partition query).
        var query = new QueryDefinition("SELECT c.id, c.userId FROM c WHERE c.type = @type AND c.endpointHash = @hash")
            .WithParameter("@type", SubscriptionType)
            .WithParameter("@hash", hash);
        foreach (var other in await QueryAsync<DocumentRef>(container, query, null, cancellationToken))
        {
            if (other.UserId != subscription.UserId)
            {
                await DeleteAsync(container, other.Id, new PartitionKey(other.UserId), cancellationToken);
            }
        }

        var document = new SubscriptionDocument
        {
            Id = SubscriptionId(hash),
            UserId = subscription.UserId,
            EndpointHash = hash,
            Endpoint = subscription.Endpoint,
            P256dh = subscription.P256dh,
            Auth = subscription.Auth,
            Language = subscription.Language,
            CreatedAt = subscription.CreatedAt,
        };
        await container.UpsertItemAsync(document, new PartitionKey(subscription.UserId), cancellationToken: cancellationToken);
    }

    public async Task<bool> RemoveAsync(string userId, string endpoint, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        return await DeleteAsync(container, SubscriptionId(Hash(endpoint)), new PartitionKey(userId), cancellationToken);
    }

    public async Task<IReadOnlyList<PushSubscription>> ListAsync(string userId, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        var query = new QueryDefinition("SELECT * FROM c WHERE c.type = @type").WithParameter("@type", SubscriptionType);
        var documents = await QueryAsync<SubscriptionDocument>(container, query, new PartitionKey(userId), cancellationToken);
        return [.. documents.OrderBy(d => d.CreatedAt).Select(d => new PushSubscription(d.UserId, d.Endpoint, d.P256dh, d.Auth, d.Language, d.CreatedAt))];
    }

    public async Task<PushSettings> GetSettingsAsync(string userId, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        var (document, _) = await ReadAsync<SettingsDocument>(container, SettingsId, new PartitionKey(userId), cancellationToken);
        return document?.Settings ?? new PushSettings();
    }

    public async Task SaveSettingsAsync(string userId, PushSettings settings, CancellationToken cancellationToken = default)
    {
        // The whole set of switches is replaced by the person's latest choice: no read, so nothing to race.
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        await container.UpsertItemAsync(new SettingsDocument { Id = SettingsId, UserId = userId, Settings = settings }, new PartitionKey(userId), cancellationToken: cancellationToken);
    }

    public async Task RemoveUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        var key = new PartitionKey(userId);
        foreach (var document in await QueryAsync<DocumentRef>(container, new QueryDefinition("SELECT c.id, c.userId FROM c"), key, cancellationToken))
        {
            await DeleteAsync(container, document.Id, key, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<string>> SubscribedUserIdsAsync(CancellationToken cancellationToken = default)
    {
        // Once an hour for the stock alert job: a cross-partition query over the device documents only.
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        var query = new QueryDefinition("SELECT VALUE c.userId FROM c WHERE c.type = @type").WithParameter("@type", SubscriptionType);
        return [.. (await QueryAsync<string>(container, query, null, cancellationToken)).Distinct(StringComparer.Ordinal)];
    }

    public async Task<StockAlertState> GetAlertStateAsync(string userId, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        var (document, _) = await ReadAsync<AlertsDocument>(container, AlertsId, new PartitionKey(userId), cancellationToken);
        return document is null
            ? StockAlertState.None
            : new StockAlertState(document.LastHandled, new StockAlertMemory(Set(document.Low), Set(document.Peak)) { PeakSoon = Set(document.PeakSoon), Gear = Set(document.Gear) });
    }

    public async Task SaveAlertStateAsync(string userId, StockAlertState state, CancellationToken cancellationToken = default)
    {
        // Written by the stock alert job only, which owns this person's state for the run: the latest result replaces the previous one.
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        var document = new AlertsDocument
        {
            Id = AlertsId,
            UserId = userId,
            LastHandled = state.LastHandled,
            Low = [.. state.Memory.Low.Order(StringComparer.Ordinal)],
            Peak = [.. state.Memory.Peak.Order(StringComparer.Ordinal)],
            PeakSoon = [.. state.Memory.PeakSoon.Order(StringComparer.Ordinal)],
            Gear = [.. state.Memory.Gear.Order(StringComparer.Ordinal)],
        };
        await container.UpsertItemAsync(document, new PartitionKey(userId), cancellationToken: cancellationToken);
    }

    public async Task<bool> TryClaimAsync(string userId, string key, DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        var partition = new PartitionKey(userId);
        var id = "claim-" + Hash(key);
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var (current, etag) = await ReadAsync<ClaimDocument>(container, id, partition, cancellationToken);

            // TTL follows the server clock, so the time is checked here too.
            if (current is not null && now - current.ClaimedAt < window)
            {
                return false;
            }

            var next = new ClaimDocument { Id = id, UserId = userId, ClaimedAt = now, Ttl = (int)Math.Ceiling((window + TtlMargin).TotalSeconds) };
            try
            {
                if (etag is null)
                {
                    await container.CreateItemAsync(next, partition, cancellationToken: cancellationToken);
                }
                else
                {
                    await container.ReplaceItemAsync(next, id, partition, new ItemRequestOptions { IfMatchEtag = etag }, cancellationToken);
                }

                return true;
            }
            catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
            {
                // Another instance claimed it at the same moment: look again (it will now be there).
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // The expired claim was removed meanwhile: try again as a new one.
            }
        }

        return false;
    }

    /// <summary>A fixed-length id for an endpoint or key: endpoints are long URLs with characters Cosmos DB ids must not hold.</summary>
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string SubscriptionId(string hash) => "sub-" + hash;

    private static HashSet<string> Set(IReadOnlyList<string>? values) => new(values ?? [], StringComparer.Ordinal);

    private static async Task<(T? Document, string? ETag)> ReadAsync<T>(Container container, string id, PartitionKey key, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            var response = await container.ReadItemAsync<T>(id, key, cancellationToken: cancellationToken);
            return (response.Resource, response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, null);
        }
    }

    private static async Task<bool> DeleteAsync(Container container, string id, PartitionKey key, CancellationToken cancellationToken)
    {
        try
        {
            await container.DeleteItemAsync<object>(id, key, cancellationToken: cancellationToken);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static async Task<List<T>> QueryAsync<T>(Container container, QueryDefinition query, PartitionKey? key, CancellationToken cancellationToken)
    {
        List<T> results = [];
        using var iterator = container.GetItemQueryIterator<T>(query, requestOptions: key is { } partition ? new QueryRequestOptions { PartitionKey = partition } : null);
        while (iterator.HasMoreResults)
        {
            results.AddRange(await iterator.ReadNextAsync(cancellationToken));
        }

        return results;
    }

    internal sealed record DocumentRef
    {
        public string Id { get; init; } = "";
        public string UserId { get; init; } = "";
    }

    internal sealed record SubscriptionDocument
    {
        public string Id { get; init; } = "";
        public string UserId { get; init; } = "";
        public string Type { get; init; } = SubscriptionType;
        public string EndpointHash { get; init; } = "";
        public string Endpoint { get; init; } = "";
        public string P256dh { get; init; } = "";
        public string Auth { get; init; } = "";
        public string Language { get; init; } = "";
        public DateTimeOffset CreatedAt { get; init; }
    }

    internal sealed record SettingsDocument
    {
        public string Id { get; init; } = "";
        public string UserId { get; init; } = "";
        public string Type { get; init; } = "settings";
        public PushSettings Settings { get; init; } = new();
    }

    internal sealed record AlertsDocument
    {
        public string Id { get; init; } = "";
        public string UserId { get; init; } = "";
        public string Type { get; init; } = "alerts";
        public DateOnly? LastHandled { get; init; }
        public IReadOnlyList<string> Low { get; init; } = [];
        public IReadOnlyList<string> Peak { get; init; } = [];
        public IReadOnlyList<string> PeakSoon { get; init; } = [];
        public IReadOnlyList<string> Gear { get; init; } = [];
    }

    internal sealed record ClaimDocument
    {
        public string Id { get; init; } = "";
        public string UserId { get; init; } = "";
        public string Type { get; init; } = "claim";
        public DateTimeOffset ClaimedAt { get; init; }

        /// <summary>Seconds until Cosmos DB removes the document.</summary>
        public int Ttl { get; init; }
    }
}
