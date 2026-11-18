using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dialed.Api.Data;
using Microsoft.Azure.Cosmos;

namespace Dialed.Api.PublicProfiles;

/// <summary>
/// Public profiles and reports in Cosmos DB (#38, #106). The <c>publicprofiles</c> container is partitioned by <c>/userId</c>: the person's
/// profile (<c>profile</c>) and the reports about them sit in their partition, so reading a profile is a point read. A report's document id is
/// a hash of who reported what, so a second report of the same thing fails to create and is counted as a duplicate.
/// The lists (open profiles, suspended people, all reports) read across partitions; they are used by the catalog build and the owner's
/// moderation screen, not on every request.
/// </summary>
public sealed class CosmosPublicProfileStore(DialedCosmos cosmos) : IPublicProfileStore
{
    public static readonly CosmosContainerSpec Container = new("publicprofiles", "/userId", DefaultTimeToLive: null);

    private const string ProfileId = "profile";

    public async Task<PublicProfile> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        try
        {
            var response = await container.ReadItemAsync<ProfileDocument>(ProfileId, new PartitionKey(userId), cancellationToken: cancellationToken);
            return response.Resource.ToProfile();
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new PublicProfile(userId);
        }
    }

    public async Task SaveAsync(PublicProfile profile, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        await container.UpsertItemAsync(ProfileDocument.From(profile), new PartitionKey(profile.UserId), cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<string>> OpenUserIdsAsync(CancellationToken cancellationToken = default)
    {
        var query = new QueryDefinition("SELECT VALUE c.userId FROM c WHERE c.type = 'profile' AND c.enabled = true AND c.suspended = false");
        return await QueryAsync<string>(query, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> SuspendedUserIdsAsync(CancellationToken cancellationToken = default)
    {
        var query = new QueryDefinition("SELECT VALUE c.userId FROM c WHERE c.type = 'profile' AND c.suspended = true");
        return [.. (await QueryAsync<string>(query, cancellationToken)).Order(StringComparer.Ordinal)];
    }

    public async Task<bool> AddReportAsync(Report report, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        try
        {
            await container.CreateItemAsync(ReportDocument.From(report), new PartitionKey(report.OwnerId), cancellationToken: cancellationToken);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            return false; // this reporter already reported this
        }
    }

    public async Task<IReadOnlyList<Report>> ReportsAsync(CancellationToken cancellationToken = default)
    {
        var documents = await QueryAsync<ReportDocument>(new QueryDefinition("SELECT * FROM c WHERE c.type = 'report'"), cancellationToken);
        return [.. documents.Select(d => d.ToReport()).OrderByDescending(r => r.CreatedAt)];
    }

    public async Task RemoveUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);

        // Their partition (profile and reports about them), then the reports they made about others.
        var query = new QueryDefinition("SELECT c.id, c.userId FROM c WHERE c.userId = @user OR (c.type = 'report' AND c.reporterId = @user)")
            .WithParameter("@user", userId);
        foreach (var document in await QueryAsync<KeyedId>(query, cancellationToken))
        {
            try
            {
                await container.DeleteItemAsync<KeyedId>(document.Id, new PartitionKey(document.UserId), cancellationToken: cancellationToken);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // already gone
            }
        }
    }

    private async Task<List<T>> QueryAsync<T>(QueryDefinition query, CancellationToken cancellationToken)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        using var iterator = container.GetItemQueryIterator<T>(query);
        var results = new List<T>();
        while (iterator.HasMoreResults)
        {
            results.AddRange(await iterator.ReadNextAsync(cancellationToken));
        }

        return results;
    }

    internal sealed record ProfileDocument
    {
        public string Id { get; init; } = ProfileId;
        public string UserId { get; init; } = "";
        public string Type { get; init; } = "profile";
        public bool Enabled { get; init; }
        public bool Suspended { get; init; }

        public static ProfileDocument From(PublicProfile profile) => new() { UserId = profile.UserId, Enabled = profile.Enabled, Suspended = profile.Suspended };

        public PublicProfile ToProfile() => new(UserId, Enabled, Suspended);
    }

    internal sealed record ReportDocument
    {
        public string Id { get; init; } = "";

        /// <summary>The reported person (the partition).</summary>
        public string UserId { get; init; } = "";

        public string Type { get; init; } = "report";
        public string ReportId { get; init; } = "";
        public string ReporterId { get; init; } = "";
        public ReportTarget Target { get; init; }
        public string? ItemId { get; init; }
        public ReportReason Reason { get; init; }
        public string? Note { get; init; }
        public DateTimeOffset CreatedAt { get; init; }

        public static ReportDocument From(Report report) => new()
        {
            Id = DocumentId(report),
            UserId = report.OwnerId,
            ReportId = report.Id,
            ReporterId = report.ReporterId,
            Target = report.Target,
            ItemId = report.ItemId,
            Reason = report.Reason,
            Note = report.Note,
            CreatedAt = report.CreatedAt,
        };

        public Report ToReport() => new(ReportId, ReporterId, UserId, Target, ItemId, Reason, Note, CreatedAt);

        /// <summary>One document per reporter, target and item: the same report twice has the same id.</summary>
        private static string DocumentId(Report report) =>
            "report-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{report.ReporterId}\n{report.Target}\n{report.ItemId}")));
    }

    private sealed record KeyedId(string Id, string UserId);
}
