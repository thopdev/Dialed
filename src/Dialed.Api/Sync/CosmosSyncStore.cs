using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Dialed.Api.Data;
using Microsoft.Azure.Cosmos;

namespace Dialed.Api.Sync;

/// <summary>
/// Synced records in Cosmos DB (#38). One <c>sync</c> container partitioned by <c>/householdId</c> (ADR-0011): a pull, a push and a
/// person's own lists all stay inside one household's partition. Only the catalog build (<see cref="ListPublicAsync"/>) and the daily purge
/// read across households.
/// <para>
/// Each record is one document. The app's JSON sits untouched under <c>entity</c> (not indexed, see <c>infra/modules/cosmos-database.bicep</c>);
/// the fields the queries filter on are copied next to it. Pulls order by <c>sortKey</c> (update time in ticks plus the record id), so the
/// watermark is one range filter on one indexed field and needs no composite index. Writes use the ETag, so two pushes of one record cannot
/// both win. Soft-deleted records carry a TTL a week past the purge window, so Cosmos DB removes them even if the purge worker never runs.
/// </para>
/// </summary>
public sealed class CosmosSyncStore(DialedCosmos cosmos, TimeProvider time) : ISyncStore
{
    public static readonly CosmosContainerSpec Container = new("sync", "/householdId");

    /// <summary>A deleted record is kept this long before Cosmos DB removes it by itself (the purge worker normally removes it earlier).</summary>
    public static readonly TimeSpan DeletedTtl = SyncPurgeWorker.Retention + TimeSpan.FromDays(7);

    private const int MaxRetries = 5;

    public async Task<(IReadOnlyList<SyncRow> Rows, bool HasMore)> PullAsync(string householdId, string viewerId, string table, DateTimeOffset? afterUpdatedAt, string? afterId, int pageSize, CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 500);
        var after = afterUpdatedAt is { } at ? SortKey(at.UtcTicks, afterId ?? "") : "";

        // Someone else's private items are left out in the query itself, so a page is always full when there is more.
        var query = new QueryDefinition(
                "SELECT TOP @take * FROM c WHERE c.table = @table AND c.sortKey > @after AND (c.visibility != @private OR c.createdBy = @viewer) ORDER BY c.sortKey")
            .WithParameter("@take", pageSize + 1)
            .WithParameter("@table", table)
            .WithParameter("@after", after)
            .WithParameter("@private", VisibilityRules.Private)
            .WithParameter("@viewer", viewerId);
        var documents = await QueryAsync(householdId, query, cancellationToken);
        return ([.. documents.Take(pageSize).Select(ToRow)], documents.Count > pageSize);
    }

    public async Task<SyncRow?> GetAsync(string householdId, string table, string id, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        var (document, _) = await ReadAsync(container, householdId, table, id, cancellationToken);
        return document is null ? null : ToRow(document);
    }

    public Task<(IReadOnlyList<SyncRow> Rows, int Total)> ListSharedAsync(string householdId, string authorId, string table, Relationship reader, int skip, int take, CancellationToken cancellationToken = default) =>
        ListSharedAsync([new SharedScope(householdId, reader)], authorId, table, skip, take, cancellationToken);

    public async Task<(IReadOnlyList<SyncRow> Rows, int Total)> ListSharedAsync(IReadOnlyList<SharedScope> scopes, string authorId, string table, int skip, int take, CancellationToken cancellationToken = default)
    {
        // One query per household (people are in a handful at most), each inside its own partition.
        var all = new List<SyncDocument>();
        foreach (var scope in scopes.DistinctBy(s => s.HouseholdId))
        {
            var readable = Enumerable.Range(VisibilityRules.Private, VisibilityRules.Public + 1).Where(v => VisibilityRules.CanRead(v, scope.Reader)).ToArray();
            if (readable.Length == 0)
            {
                continue;
            }

            var query = new QueryDefinition(
                    "SELECT * FROM c WHERE c.table = @table AND c.createdBy = @author AND c.deleted = false AND ARRAY_CONTAINS(@readable, c.visibility)")
                .WithParameter("@table", table)
                .WithParameter("@author", authorId)
                .WithParameter("@readable", readable);
            all.AddRange(await QueryAsync(scope.HouseholdId, query, cancellationToken));
        }

        var ordered = all.OrderByDescending(d => d.Ts).ThenBy(d => d.RowId, StringComparer.Ordinal).ToList();
        return ([.. ordered.Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 100)).Select(ToRow)], ordered.Count);
    }

    public async Task<IReadOnlyList<SyncRow>> ListAuthoredAsync(string householdId, string authorId, string table, CancellationToken cancellationToken = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.table = @table AND c.createdBy = @author AND c.deleted = false ORDER BY c.sortKey")
            .WithParameter("@table", table)
            .WithParameter("@author", authorId);
        return [.. (await QueryAsync(householdId, query, cancellationToken)).Select(ToRow)];
    }

    public async Task<IReadOnlyList<SyncRow>> ListPublicAsync(string table, CancellationToken cancellationToken = default)
    {
        // The one cross-partition read of the API's normal work; only the catalog build (#105) calls it.
        var query = new QueryDefinition("SELECT * FROM c WHERE c.table = @table AND c.visibility = @public AND c.deleted = false")
            .WithParameter("@table", table)
            .WithParameter("@public", VisibilityRules.Public);
        var documents = await QueryAsync(null, query, cancellationToken);
        return [.. documents.OrderBy(d => d.HouseholdId, StringComparer.Ordinal).ThenBy(d => d.RowId, StringComparer.Ordinal).Select(ToRow)];
    }

    public async Task DeleteHouseholdAsync(string householdId, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        var ids = await QueryAsync<IdOnly>(container, householdId, new QueryDefinition("SELECT c.id FROM c"), cancellationToken);
        foreach (var id in ids)
        {
            await DeleteIfThereAsync(container, id.Id, new PartitionKey(householdId), cancellationToken);
        }
    }

    public async Task AnonymiseAuthorAsync(string householdId, string authorId, string replacement, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        var key = new PartitionKey(householdId);
        var query = new QueryDefinition("SELECT c.id FROM c WHERE c.createdBy = @author OR c.updatedBy = @author").WithParameter("@author", authorId);
        foreach (var id in await QueryAsync<IdOnly>(container, householdId, query, cancellationToken))
        {
            for (var attempt = 0; attempt < MaxRetries; attempt++)
            {
                var (document, etag) = await ReadAsync(container, key, id.Id, cancellationToken);
                if (document is null)
                {
                    break;
                }

                var entity = (JsonObject)document.Entity.DeepClone();
                foreach (var field in new[] { "createdBy", "updatedBy" })
                {
                    if (entity[field]?.GetValue<string>() == authorId)
                    {
                        entity[field] = replacement;
                    }
                }

                var changed = document with
                {
                    Entity = entity,
                    CreatedBy = document.CreatedBy == authorId ? replacement : document.CreatedBy,
                    UpdatedBy = document.UpdatedBy == authorId ? replacement : document.UpdatedBy,
                };
                if (await TryWriteAsync(container, changed, key, etag, cancellationToken))
                {
                    break;
                }
            }
        }
    }

    public async Task<IReadOnlyDictionary<string, int>> PurgeDeletedAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        // Once a day, across households: only the few deleted records are read, through the index on deleted and ts.
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        var query = new QueryDefinition("SELECT c.id, c.householdId, c.table FROM c WHERE c.deleted = true AND c.ts < @cutoff")
            .WithParameter("@cutoff", cutoff.UtcTicks);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var doomed in await QueryAsync<PurgeCandidate>(container, null, query, cancellationToken))
        {
            if (await DeleteIfThereAsync(container, doomed.Id, new PartitionKey(doomed.HouseholdId), cancellationToken))
            {
                counts[doomed.Table] = counts.GetValueOrDefault(doomed.Table) + 1;
            }
        }

        return counts;
    }

    public async Task<StoreResult> PushAsync(string householdId, string userId, string table, string id, JsonObject entity, string? ifMatch, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || entity["id"]?.GetValue<string>() is not { } bodyId || bodyId != id)
        {
            return new StoreResult(StoreOutcome.Invalid, Message: "The record id must match the URL.");
        }

        if (entity["householdId"]?.GetValue<string>() is { Length: > 0 } claimed && claimed != householdId)
        {
            return new StoreResult(StoreOutcome.Forbidden, Message: "No access to that household.");
        }

        if (id.IndexOfAny(['/', '\\', '?', '#']) >= 0)
        {
            return new StoreResult(StoreOutcome.Invalid, Message: "The record id has characters that are not allowed.");
        }

        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        var key = new PartitionKey(householdId);
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var (document, etag) = await ReadAsync(container, key, DocumentId(table, id), cancellationToken);
            var current = document is null ? null : ToRow(document);

            // The same rules as in memory, in the same order: a hidden record is not revealed, then the version must match.
            if (current is not null && !VisibilityRules.CanRead(VisibilityRules.Of(current.Entity), VisibilityRules.InHousehold(current.Entity, userId)))
            {
                return new StoreResult(StoreOutcome.Forbidden, Message: "No access to that record.");
            }

            if (current is not null && ifMatch != current.Version.ToString(CultureInfo.InvariantCulture))
            {
                return new StoreResult(StoreOutcome.VersionMismatch, current);
            }

            if (current is null && ifMatch is not null)
            {
                return new StoreResult(StoreOutcome.Invalid, Message: "Unknown version.");
            }

            var now = time.GetUtcNow();
            var version = current is null ? 1 : current.Version + 1;
            var stored = (JsonObject)entity.DeepClone();
            stored["householdId"] = householdId;
            stored["updatedBy"] = userId;
            stored["updatedAt"] = now;
            stored["version"] = version;
            if (current is null)
            {
                stored["createdBy"] = userId;
                stored["createdAt"] = now;
            }
            else
            {
                stored["createdBy"] = current.Entity["createdBy"]?.DeepClone();
                stored["createdAt"] = current.Entity["createdAt"]?.DeepClone();
            }

            var next = ToDocument(householdId, table, id, now, version, stored);
            if (await TryWriteAsync(container, next, key, etag, cancellationToken))
            {
                return new StoreResult(StoreOutcome.Stored, ToRow(next));
            }

            // Another push of this record landed first: read it again, which usually ends in a version mismatch for this one.
        }

        var latest = await GetAsync(householdId, table, id, cancellationToken);
        return new StoreResult(StoreOutcome.VersionMismatch, latest);
    }

    /// <summary>Update time and id in one string that sorts the way pulls page: zero-padded ticks, then the id.</summary>
    internal static string SortKey(long ticks, string id) => ticks.ToString("D19", CultureInfo.InvariantCulture) + "|" + id;

    /// <summary>Record ids are only unique per table, and a document id per partition, so the table goes in front.</summary>
    internal static string DocumentId(string table, string id) => table + ":" + id;

    private static SyncDocument ToDocument(string householdId, string table, string id, DateTimeOffset updatedAt, int version, JsonObject entity)
    {
        var deleted = entity["deleted"] is JsonValue d && d.TryGetValue<bool>(out var gone) && gone;
        return new SyncDocument
        {
            Id = DocumentId(table, id),
            HouseholdId = householdId,
            Table = table,
            RowId = id,
            Ts = updatedAt.UtcTicks,
            SortKey = SortKey(updatedAt.UtcTicks, id),
            Version = version,
            Deleted = deleted,
            CreatedBy = entity["createdBy"] is JsonValue c && c.TryGetValue<string>(out var createdBy) ? createdBy : null,
            UpdatedBy = entity["updatedBy"] is JsonValue u && u.TryGetValue<string>(out var updatedBy) ? updatedBy : null,
            Visibility = VisibilityRules.Of(entity),
            Entity = entity,
            Ttl = deleted ? (int)DeletedTtl.TotalSeconds : null,
        };
    }

    private static SyncRow ToRow(SyncDocument document) =>
        new(document.RowId, document.HouseholdId, new DateTimeOffset(document.Ts, TimeSpan.Zero), document.Version, document.Entity);

    private async Task<List<SyncDocument>> QueryAsync(string? householdId, QueryDefinition query, CancellationToken cancellationToken)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        return await QueryAsync<SyncDocument>(container, householdId, query, cancellationToken);
    }

    /// <summary>Runs a query inside one household's partition, or across all of them when <paramref name="householdId"/> is null.</summary>
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

    private static Task<(SyncDocument? Document, string? ETag)> ReadAsync(Container container, string householdId, string table, string id, CancellationToken cancellationToken) =>
        ReadAsync(container, new PartitionKey(householdId), DocumentId(table, id), cancellationToken);

    private static async Task<(SyncDocument? Document, string? ETag)> ReadAsync(Container container, PartitionKey key, string documentId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await container.ReadItemAsync<SyncDocument>(documentId, key, cancellationToken: cancellationToken);
            return (response.Resource, response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, null);
        }
    }

    /// <summary>Creates the document when there was none, otherwise replaces the version that was read. False when another writer was first.</summary>
    private static async Task<bool> TryWriteAsync(Container container, SyncDocument document, PartitionKey key, string? etag, CancellationToken cancellationToken)
    {
        try
        {
            if (etag is null)
            {
                await container.CreateItemAsync(document, key, cancellationToken: cancellationToken);
            }
            else
            {
                await container.ReplaceItemAsync(document, document.Id, key, new ItemRequestOptions { IfMatchEtag = etag }, cancellationToken);
            }

            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static async Task<bool> DeleteIfThereAsync(Container container, string id, PartitionKey key, CancellationToken cancellationToken)
    {
        try
        {
            await container.DeleteItemAsync<SyncDocument>(id, key, cancellationToken: cancellationToken);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false; // already gone (a second purge, or TTL was first)
        }
    }

    internal sealed record SyncDocument
    {
        public string Id { get; init; } = "";
        public string HouseholdId { get; init; } = "";
        public string Table { get; init; } = "";

        /// <summary>The record's own id (the document id has the table in front).</summary>
        public string RowId { get; init; } = "";

        /// <summary>Update time in UTC ticks: exact, and compared as a number by the purge.</summary>
        public long Ts { get; init; }

        public string SortKey { get; init; } = "";
        public int Version { get; init; }
        public bool Deleted { get; init; }
        public string? CreatedBy { get; init; }
        public string? UpdatedBy { get; init; }
        public int Visibility { get; init; }
        public JsonObject Entity { get; init; } = [];

        /// <summary>Seconds until Cosmos DB removes the document; only set on deleted records.</summary>
        public int? Ttl { get; init; }
    }

    private sealed record IdOnly(string Id);

    private sealed record PurgeCandidate(string Id, string HouseholdId, string Table);
}
