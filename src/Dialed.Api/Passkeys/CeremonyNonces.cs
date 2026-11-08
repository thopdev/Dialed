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
