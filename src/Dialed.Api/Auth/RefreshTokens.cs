using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Dialed.Api.Auth;

/// <summary>A refresh token as stored: only its hash, never the token itself.</summary>
public sealed record RefreshTokenRecord(string TokenHash, string UserId, string FamilyId, DateTimeOffset ExpiresAt, bool Used = false, bool Revoked = false);

public interface IRefreshTokenStore
{
    Task AddAsync(RefreshTokenRecord record, CancellationToken cancellationToken = default);

    Task<RefreshTokenRecord?> FindAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>Marks the token as used. Returns <c>false</c> when it was already used (so two requests cannot both rotate it).</summary>
    Task<bool> TryConsumeAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>Revokes every token of the sign-in (the token family).</summary>
    Task RevokeFamilyAsync(string familyId, CancellationToken cancellationToken = default);

    /// <summary>Revokes every token of the person, on every device (account deletion).</summary>
    Task RevokeUserAsync(string userId, CancellationToken cancellationToken = default);
}

public sealed class InMemoryRefreshTokenStore : IRefreshTokenStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, RefreshTokenRecord> records = new(StringComparer.Ordinal);

    /// <summary>The stored hashes, for tests that check no raw token is kept.</summary>
    public IReadOnlyList<string> Hashes
    {
        get
        {
            lock (gate)
            {
                return [.. records.Keys];
            }
        }
    }

    public Task AddAsync(RefreshTokenRecord record, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            records[record.TokenHash] = record;
        }

        return Task.CompletedTask;
    }

    public Task<RefreshTokenRecord?> FindAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(records.GetValueOrDefault(tokenHash));
        }
    }

    public Task<bool> TryConsumeAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (!records.TryGetValue(tokenHash, out var record) || record.Used)
            {
                return Task.FromResult(false);
            }

            records[tokenHash] = record with { Used = true };
            return Task.FromResult(true);
        }
    }

    public Task RevokeUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            foreach (var (hash, record) in records.Where(r => r.Value.UserId == userId).ToList())
            {
                records[hash] = record with { Revoked = true };
            }
        }

        return Task.CompletedTask;
    }

    public Task RevokeFamilyAsync(string familyId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            foreach (var (hash, record) in records.Where(r => r.Value.FamilyId == familyId).ToList())
            {
                records[hash] = record with { Revoked = true };
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Issues, rotates and revokes refresh tokens. Each use hands out a new token and retires the old one; using a retired token
/// again means it was copied, so the whole sign-in (token family) is revoked.
/// </summary>
public sealed class RefreshTokenService(IRefreshTokenStore store, IUserDirectory users, TokenIssuer tokens, IOptions<JwtOptions> options, TimeProvider time)
{
    private readonly JwtOptions options = options.Value;

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Starts a new sign-in (a new token family) for the user.</summary>
    public async Task<TokenResponse> SignInAsync(AppUser user, CancellationToken cancellationToken = default) =>
        await IssueAsync(user, Guid.CreateVersion7().ToString(), cancellationToken);

    /// <summary>Exchanges a refresh token for a new pair. <c>null</c> when the token is unknown, expired, revoked or already used.</summary>
    public async Task<TokenResponse?> RotateAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        var hash = Hash(refreshToken);
        var record = await store.FindAsync(hash, cancellationToken);
        if (record is null || record.Revoked || record.ExpiresAt <= time.GetUtcNow())
        {
            return null;
        }

        if (!await store.TryConsumeAsync(hash, cancellationToken))
        {
            await store.RevokeFamilyAsync(record.FamilyId, cancellationToken);
            return null;
        }

        var user = await users.FindByIdAsync(record.UserId, cancellationToken);
        return user is null ? null : await IssueAsync(user, record.FamilyId, cancellationToken);
    }

    /// <summary>Signs out of this device: the token and the rest of its family stop working.</summary>
    public async Task RevokeAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(refreshToken) && await store.FindAsync(Hash(refreshToken), cancellationToken) is { } record)
        {
            await store.RevokeFamilyAsync(record.FamilyId, cancellationToken);
        }
    }

    private async Task<TokenResponse> IssueAsync(AppUser user, string familyId, CancellationToken cancellationToken)
    {
        var refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        await store.AddAsync(
            new RefreshTokenRecord(Hash(refreshToken), user.Id, familyId, time.GetUtcNow().Add(options.RefreshTokenLifetime)),
            cancellationToken);

        var (access, expires) = tokens.IssueAccessToken(user);
        return new TokenResponse(access, expires, refreshToken, user.Id, user.DisplayName, user.HouseholdId);
    }
}
