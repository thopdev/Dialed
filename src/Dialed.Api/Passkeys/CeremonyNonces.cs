using Dialed.Api.Data;

namespace Dialed.Api.Passkeys;

/// <summary>Makes ceremony state single use: the first completion claims its nonce, replays do not get it.</summary>
public interface ICeremonyNonces
{
    Task<bool> TryClaimAsync(string nonce, TimeSpan keepFor, CancellationToken cancellationToken = default);
}

public sealed class InMemoryCeremonyNonces(TimeProvider time) : ICeremonyNonces
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, DateTimeOffset> claimed = new(StringComparer.Ordinal);

    public Task<bool> TryClaimAsync(string nonce, TimeSpan keepFor, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            foreach (var expired in claimed.Where(c => c.Value <= now).Select(c => c.Key).ToList())
            {
                claimed.Remove(expired);
            }

            return Task.FromResult(claimed.TryAdd(nonce, now.Add(keepFor)));
        }
    }
}

/// <summary>
/// Claimed nonces in Cosmos DB, so a ceremony token cannot be replayed against another API instance or after a restart.
/// One <c>ceremony:{nonce}</c> document per claim in the <c>identity</c> container: creating it claims the nonce (409 on a replay),
/// and its TTL removes it once the token has expired. An expired claim that TTL has not removed yet may be claimed again, like in memory.
/// </summary>
public sealed class CosmosCeremonyNonces(DialedCosmos cosmos, TimeProvider time) : ICeremonyNonces
{
    public async Task<bool> TryClaimAsync(string nonce, TimeSpan keepFor, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(cancellationToken);
        var now = time.GetUtcNow();
        var claim = new NonceDocument { Id = "ceremony:" + CosmosIdentity.Hash(nonce), Type = "ceremony", ExpiresAt = now.Add(keepFor), Ttl = CosmosIdentity.Ttl(keepFor) };
        for (var attempt = 0; attempt < CosmosIdentity.MaxRetries; attempt++)
        {
            var (existing, etag) = await container.ReadAsync<NonceDocument>(claim.Id, cancellationToken);
            if (existing is not null && existing.ExpiresAt > now)
            {
                return false;
            }

            if (await container.TryWriteAsync(claim, etag, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    internal sealed record NonceDocument : IdentityDocument
    {
        public DateTimeOffset ExpiresAt { get; init; }
    }
}
