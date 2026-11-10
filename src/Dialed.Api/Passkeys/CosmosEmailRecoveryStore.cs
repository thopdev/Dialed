using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dialed.Api.Data;
using Microsoft.Azure.Cosmos;

namespace Dialed.Api.Passkeys;

/// <summary>
/// Email recovery state in Cosmos DB (#197), so the lockout, the send cooldown and the email codes hold across restarts and
/// API instances. The <c>recovery</c> container is partitioned by <c>/accountId</c>; each account has at most three small
/// documents (<c>code</c>, <c>sent</c>, <c>failures</c>), so every call is a point read or write. Documents carry a TTL, so Cosmos DB
/// removes them once they no longer matter; reads still check the time themselves, because TTL follows the server clock.
/// Concurrent writers are kept apart with ETags: a write that lost the race reads again and retries.
/// </summary>
public sealed class CosmosEmailRecoveryStore(DialedCosmos cosmos, TimeProvider time) : IEmailRecoveryStore
{
    /// <summary>One logical partition per account: all of an account's recovery state is read and written together.</summary>
    public static readonly CosmosContainerSpec Container = new("recovery", "/accountId");

    private const string CodeId = "code";
    private const string SentId = "sent";
    private const string FailuresId = "failures";
    private const int MaxRetries = 5;

    /// <summary>Extra time a document is kept after it stops mattering, so TTL never removes it early.</summary>
    private static readonly TimeSpan TtlMargin = TimeSpan.FromMinutes(1);

    public async Task<bool> TryBeginSendAsync(string accountId, DateTimeOffset now, TimeSpan cooldown, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        var key = new PartitionKey(accountId);
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var (last, etag) = await ReadAsync<SentDocument>(container, SentId, key, cancellationToken);
            if (last is not null && now - last.SentAt < cooldown)
            {
                return false;
            }

            var next = new SentDocument { Id = SentId, AccountId = accountId, SentAt = now, Ttl = Ttl(cooldown) };
            if (await TryWriteAsync(container, next, key, etag, cancellationToken))
            {
                return true;
            }
        }

        return false; // lost every race: someone else is sending right now
    }

    public async Task<bool> IsLockedAsync(string accountId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        var (failures, _) = await ReadAsync<FailuresDocument>(container, FailuresId, new PartitionKey(accountId), cancellationToken);
        return failures is not null && now - failures.Since < EmailRecoveryLimits.FailureWindow && failures.Count >= EmailRecoveryLimits.MaxFailures;
    }

    public async Task RecordFailureAsync(string accountId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        var key = new PartitionKey(accountId);
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var (current, etag) = await ReadAsync<FailuresDocument>(container, FailuresId, key, cancellationToken);
            var next = current is not null && now - current.Since < EmailRecoveryLimits.FailureWindow
                ? current with { Count = current.Count + 1 }
                : new FailuresDocument { Id = FailuresId, AccountId = accountId, Count = 1, Since = now };
            next = next with { Ttl = Ttl(next.Since + EmailRecoveryLimits.FailureWindow - now) };
            if (await TryWriteAsync(container, next, key, etag, cancellationToken))
            {
                return;
            }
        }

        throw new InvalidOperationException("Could not record the failed recovery attempt.");
    }

    public async Task PutAsync(string accountId, string codeHash, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        var document = new CodeDocument { Id = CodeId, AccountId = accountId, Hash = codeHash, ExpiresAt = expiresAt, Attempts = 0, Ttl = Ttl(expiresAt - time.GetUtcNow()) };
        await container.UpsertItemAsync(document, new PartitionKey(accountId), cancellationToken: cancellationToken);
    }

    public async Task<bool> CheckAsync(string accountId, string codeHash, DateTimeOffset now, bool consume, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(Container, cancellationToken);
        var key = new PartitionKey(accountId);
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var (entry, etag) = await ReadAsync<CodeDocument>(container, CodeId, key, cancellationToken);
            if (entry is null || entry.ExpiresAt <= now || entry.Attempts >= EmailRecoveryLimits.MaxAttempts)
            {
                return false;
            }

            var options = new ItemRequestOptions { IfMatchEtag = etag };
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(entry.Hash), Encoding.UTF8.GetBytes(codeHash)))
                {
                    await container.ReplaceItemAsync(entry with { Attempts = entry.Attempts + 1 }, CodeId, key, options, cancellationToken);
                    return false;
                }

                if (consume)
                {
                    // Only one caller can delete this version: a second one using the same code gets a conflict and loses.
                    await container.DeleteItemAsync<CodeDocument>(CodeId, key, options, cancellationToken);
                }

                return true;
            }
            catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
            {
                // Someone changed or used the code at the same time: look again.
            }
        }

        return false;
    }

    private static int Ttl(TimeSpan untilIrrelevant) => (int)Math.Ceiling(Math.Max(0, (untilIrrelevant + TtlMargin).TotalSeconds));

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

    /// <summary>Creates the document when there was none, otherwise replaces the version that was read. False when another writer was first.</summary>
    private static async Task<bool> TryWriteAsync<T>(Container container, T document, PartitionKey key, string? etag, CancellationToken cancellationToken)
        where T : RecoveryDocument
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

    internal abstract record RecoveryDocument
    {
        public string Id { get; init; } = "";
        public string AccountId { get; init; } = "";

        /// <summary>Seconds until Cosmos DB removes the document.</summary>
        public int Ttl { get; init; }
    }

    internal sealed record CodeDocument : RecoveryDocument
    {
        public string Hash { get; init; } = "";
        public DateTimeOffset ExpiresAt { get; init; }
        public int Attempts { get; init; }
    }

    internal sealed record SentDocument : RecoveryDocument
    {
        public DateTimeOffset SentAt { get; init; }
    }

    internal sealed record FailuresDocument : RecoveryDocument
    {
        public int Count { get; init; }
        public DateTimeOffset Since { get; init; }
    }
}
