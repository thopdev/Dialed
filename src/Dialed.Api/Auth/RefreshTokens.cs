using System.Security.Cryptography;
using System.Text;
using Dialed.Api.Data;
using Microsoft.Azure.Cosmos;
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
/// Refresh tokens in Cosmos DB, so sign-ins survive restarts and work on every API instance. One <c>refresh:{hash}</c> document
/// per token in the <c>identity</c> container: the refresh call is a point read plus a write with the ETag, so two requests
/// with the same token cannot both rotate it. Revoking a family or a person is a query (sign-out, reuse and account deletion
/// only). TTL removes a token a while after it expires.
/// </summary>
public sealed class CosmosRefreshTokenStore(DialedCosmos cosmos, TimeProvider time) : IRefreshTokenStore
{
    public async Task AddAsync(RefreshTokenRecord record, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(cancellationToken);
        var document = TokenDocument.From(record, CosmosIdentity.Ttl(record.ExpiresAt - time.GetUtcNow()));
        await container.UpsertItemAsync(document, new PartitionKey(document.Pk), cancellationToken: cancellationToken);
    }

    public async Task<RefreshTokenRecord?> FindAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(cancellationToken);
        var (document, _) = await container.ReadAsync<TokenDocument>(TokenDocument.IdFor(tokenHash), cancellationToken);
        return document?.ToRecord();
    }

    public async Task<bool> TryConsumeAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(cancellationToken);
        for (var attempt = 0; attempt < CosmosIdentity.MaxRetries; attempt++)
        {
            var (document, etag) = await container.ReadAsync<TokenDocument>(TokenDocument.IdFor(tokenHash), cancellationToken);
            if (document is null || document.Used)
            {
                return false;
            }

            // Only one writer can replace this version; the loser reads again and finds it used.
            if (await container.TryWriteAsync(document with { Used = true }, etag, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    public Task RevokeFamilyAsync(string familyId, CancellationToken cancellationToken = default) =>
        RevokeWhereAsync(new QueryDefinition("SELECT VALUE c.id FROM c WHERE c.type = 'refresh' AND c.familyId = @value").WithParameter("@value", familyId), cancellationToken);

    public Task RevokeUserAsync(string userId, CancellationToken cancellationToken = default) =>
        RevokeWhereAsync(new QueryDefinition("SELECT VALUE c.id FROM c WHERE c.type = 'refresh' AND c.userId = @value").WithParameter("@value", userId), cancellationToken);

    private async Task RevokeWhereAsync(QueryDefinition query, CancellationToken cancellationToken)
    {
        var container = await cosmos.ContainerAsync(cancellationToken);
        foreach (var id in await container.QueryAsync<string>(query, cancellationToken))
        {
            for (var attempt = 0; attempt < CosmosIdentity.MaxRetries; attempt++)
            {
                var (document, etag) = await container.ReadAsync<TokenDocument>(id, cancellationToken);
                if (document is null || document.Revoked || await container.TryWriteAsync(document with { Revoked = true }, etag, cancellationToken))
                {
                    break;
                }
            }
        }
    }

    internal sealed record TokenDocument : IdentityDocument
    {
        public string TokenHash { get; init; } = "";
        public string UserId { get; init; } = "";
        public string FamilyId { get; init; } = "";
        public DateTimeOffset ExpiresAt { get; init; }
        public bool Used { get; init; }
        public bool Revoked { get; init; }

        public static string IdFor(string tokenHash) => "refresh:" + tokenHash;

        public static TokenDocument From(RefreshTokenRecord record, int ttl) => new()
        {
            Id = IdFor(record.TokenHash),
            Type = "refresh",
            TokenHash = record.TokenHash,
            UserId = record.UserId,
            FamilyId = record.FamilyId,
            ExpiresAt = record.ExpiresAt,
            Used = record.Used,
            Revoked = record.Revoked,
            Ttl = ttl,
        };

        public RefreshTokenRecord ToRecord() => new(TokenHash, UserId, FamilyId, ExpiresAt, Used, Revoked);
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
