using System.Net;
using Dialed.Api.Data;
using Microsoft.Azure.Cosmos;

namespace Dialed.Api.Ai;

/// <summary>AI calls counted per user, operation and UTC day (#88). The gate decides; the store only counts.</summary>
public interface IAiQuotaStore
{
    /// <summary>Counts one call when fewer than <paramref name="limit"/> were made that day: the new count, or <c>null</c> when the quota is used up (nothing is counted then).</summary>
    Task<int?> TryTakeAsync(string userId, string operation, DateOnly day, int limit, CancellationToken cancellationToken = default);

    /// <summary>Gives one call back (never below zero).</summary>
    Task RefundAsync(string userId, string operation, DateOnly day, CancellationToken cancellationToken = default);

    Task<int> UsedAsync(string userId, string operation, DateOnly day, CancellationToken cancellationToken = default);
}

public sealed class InMemoryAiQuotaStore : IAiQuotaStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);

    public Task<int?> TryTakeAsync(string userId, string operation, DateOnly day, int limit, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var key = Key(userId, operation, day);
            var used = counts.GetValueOrDefault(key);
            if (used >= limit)
            {
                return Task.FromResult<int?>(null);
            }

            counts[key] = used + 1;
            return Task.FromResult<int?>(used + 1);
        }
    }

    public Task RefundAsync(string userId, string operation, DateOnly day, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var key = Key(userId, operation, day);
            if (counts.GetValueOrDefault(key) > 0)
            {
                counts[key]--;
            }
        }

        return Task.CompletedTask;
    }

    public Task<int> UsedAsync(string userId, string operation, DateOnly day, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(counts.GetValueOrDefault(Key(userId, operation, day)));
        }
    }

    private static string Key(string userId, string operation, DateOnly day) => $"{userId}|{operation}|{day:yyyyMMdd}";
}

/// <summary>
/// The AI quota in Cosmos DB, so a restart or a second instance does not hand out a fresh quota. The <c>aiquota</c> container is partitioned
/// by <c>/userId</c>; one small document per operation and UTC day (<c>label-20261118</c>), so every call is a point read and write. Writes
/// use the ETag (a lost race reads again), and each document expires through TTL two days after its day began, so old days clean themselves up.
/// </summary>
public sealed class CosmosAiQuotaStore(DialedCosmos cosmos) : IAiQuotaStore
{
    public static readonly CosmosContainerSpec Container = new("aiquota", "/userId");

    /// <summary>A day's counter is kept until the day after it ended (the day plus a margin), then Cosmos DB removes it.</summary>
    internal const int TtlSeconds = 2 * 24 * 60 * 60;

    private const int MaxRetries = 10;

    public async Task<int?> TryTakeAsync(string userId, string operation, DateOnly day, int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return null;
        }

        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        var key = new PartitionKey(userId);
        var id = Id(operation, day);
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var (current, etag) = await ReadAsync(container, id, key, cancellationToken);
            var used = current?.Count ?? 0;
            if (used >= limit)
            {
                return null;
            }

            var next = new CounterDocument { Id = id, UserId = userId, Count = used + 1, Ttl = TtlSeconds };
            if (await TryWriteAsync(container, next, key, etag, cancellationToken))
            {
                return next.Count;
            }
        }

        throw new InvalidOperationException("The AI quota could not be counted: too many calls at once.");
    }

    public async Task RefundAsync(string userId, string operation, DateOnly day, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        var key = new PartitionKey(userId);
        var id = Id(operation, day);
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var (current, etag) = await ReadAsync(container, id, key, cancellationToken);
            if (current is null || current.Count <= 0)
            {
                return;
            }

            if (await TryWriteAsync(container, current with { Count = current.Count - 1, Ttl = TtlSeconds }, key, etag, cancellationToken))
            {
                return;
            }
        }

        // Losing a refund only costs the person one call; it never fails the request.
    }

    public async Task<int> UsedAsync(string userId, string operation, DateOnly day, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        var (current, _) = await ReadAsync(container, Id(operation, day), new PartitionKey(userId), cancellationToken);
        return current?.Count ?? 0;
    }

    private static string Id(string operation, DateOnly day) =>
        $"{operation}-{day.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)}";

    private static async Task<(CounterDocument? Document, string? ETag)> ReadAsync(Container container, string id, PartitionKey key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await container.ReadItemAsync<CounterDocument>(id, key, cancellationToken: cancellationToken);
            return (response.Resource, response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, null);
        }
    }

    /// <summary>Creates the counter when there was none, otherwise replaces the version that was read. False when another writer was first.</summary>
    private static async Task<bool> TryWriteAsync(Container container, CounterDocument document, PartitionKey key, string? etag, CancellationToken cancellationToken)
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

    internal sealed record CounterDocument
    {
        public string Id { get; init; } = "";
        public string UserId { get; init; } = "";
        public int Count { get; init; }

        /// <summary>Seconds until Cosmos DB removes the document.</summary>
        public int Ttl { get; init; }
    }
}
