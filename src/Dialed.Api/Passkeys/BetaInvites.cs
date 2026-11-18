using System.Security.Cryptography;
using Dialed.Api.Data;
using Microsoft.Extensions.Options;

namespace Dialed.Api.Passkeys;

/// <summary>Configuration section <c>Beta</c>: the sign-up gate (#113).</summary>
public sealed class BetaOptions
{
    public const string SectionName = "Beta";

    /// <summary>Feature flag. When <c>true</c> sign-up needs a beta invite code; set it to <c>false</c> to open sign-up.</summary>
    public bool InviteRequired { get; set; }

    /// <summary>Secret for <c>POST /admin/invites</c>. When empty the endpoint does not exist.</summary>
    public string? AdminKey { get; set; }
}

public sealed record BetaInvite(string Code, int MaxUses, int Uses, DateTimeOffset ExpiresAt);

public sealed record CreateInviteRequest(int? MaxUses, int? DaysValid, string? Code);

public interface IBetaInviteStore
{
    Task AddAsync(BetaInvite invite, CancellationToken cancellationToken = default);

    /// <summary>Whether the code exists, has not expired and has uses left. Does not use it up.</summary>
    Task<bool> IsValidAsync(string code, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Uses one use of the code. <c>false</c> when it is unknown, expired or used up (two sign-ups cannot share the last use).</summary>
    Task<bool> TryUseAsync(string code, DateTimeOffset now, CancellationToken cancellationToken = default);
}

public sealed class InMemoryBetaInviteStore : IBetaInviteStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, BetaInvite> invites = new(StringComparer.OrdinalIgnoreCase);

    public Task AddAsync(BetaInvite invite, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            invites[invite.Code] = invite;
        }

        return Task.CompletedTask;
    }

    public Task<bool> IsValidAsync(string code, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(invites.TryGetValue(code.Trim(), out var invite) && invite.ExpiresAt > now && invite.Uses < invite.MaxUses);
        }
    }

    public Task<bool> TryUseAsync(string code, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (!invites.TryGetValue(code.Trim(), out var invite) || invite.ExpiresAt <= now || invite.Uses >= invite.MaxUses)
            {
                return Task.FromResult(false);
            }

            invites[invite.Code] = invite with { Uses = invite.Uses + 1 };
            return Task.FromResult(true);
        }
    }
}

/// <summary>
/// Beta invites in Cosmos DB: one <c>invite:{sha256(CODE)}</c> document in the <c>identity</c> container, so checking a code is a point read.
/// Uses are counted with ETags, so two sign-ups cannot share the last use. TTL removes the invite a while after it expires.
/// </summary>
public sealed class CosmosBetaInviteStore(DialedCosmos cosmos, TimeProvider time) : IBetaInviteStore
{
    /// <summary>Kept a day past expiry, so a late sign-up still finds it (and is told it expired).</summary>
    private static readonly TimeSpan KeepAfterExpiry = TimeSpan.FromDays(1);

    public async Task AddAsync(BetaInvite invite, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(cancellationToken);
        await container.UpsertItemAsync(ToDocument(invite), new Microsoft.Azure.Cosmos.PartitionKey(Id(invite.Code)), cancellationToken: cancellationToken);
    }

    public async Task<bool> IsValidAsync(string code, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(cancellationToken);
        var (invite, _) = await container.ReadAsync<InviteDocument>(Id(code), cancellationToken);
        return invite is not null && invite.ExpiresAt > now && invite.Uses < invite.MaxUses;
    }

    public async Task<bool> TryUseAsync(string code, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var container = await cosmos.ContainerAsync(cancellationToken);
        for (var attempt = 0; attempt < CosmosIdentity.MaxRetries * 2; attempt++)
        {
            var (invite, etag) = await container.ReadAsync<InviteDocument>(Id(code), cancellationToken);
            if (invite is null || invite.ExpiresAt <= now || invite.Uses >= invite.MaxUses)
            {
                return false;
            }

            if (await container.TryWriteAsync(invite with { Uses = invite.Uses + 1 }, etag, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Hashed: an admin-chosen code may hold characters a Cosmos DB id cannot.</summary>
    private static string Id(string code) => "invite:" + CosmosIdentity.Hash(code.Trim().ToUpperInvariant());

    private InviteDocument ToDocument(BetaInvite invite) => new()
    {
        Id = Id(invite.Code),
        Type = "invite",
        Code = invite.Code,
        MaxUses = invite.MaxUses,
        Uses = invite.Uses,
        ExpiresAt = invite.ExpiresAt,
        Ttl = CosmosIdentity.Ttl(invite.ExpiresAt - time.GetUtcNow() + KeepAfterExpiry),
    };

    internal sealed record InviteDocument : IdentityDocument
    {
        public string Code { get; init; } = "";
        public int MaxUses { get; init; }
        public int Uses { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
    }
}

/// <summary>The secret that guards the <c>/admin</c> endpoints (the <c>X-Admin-Key</c> header). When none is configured they do not exist.</summary>
public static class AdminKey
{
    /// <summary><c>null</c> when the key is right; otherwise the answer to send (404 without a configured key, 401 for a wrong one).</summary>
    public static IResult? Check(HttpContext http, BetaOptions options)
    {
        var key = options.AdminKey;
        if (string.IsNullOrEmpty(key))
        {
            return Results.NotFound();
        }

        var given = http.Request.Headers["X-Admin-Key"].ToString();
        return CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(given), System.Text.Encoding.UTF8.GetBytes(key))
            ? null
            : Results.Unauthorized();
    }
}

public static class BetaInviteExtensions
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static IServiceCollection AddDialedBetaInvites(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BetaOptions>(configuration.GetSection(BetaOptions.SectionName));
        services.AddCosmosOrInMemory<IBetaInviteStore, CosmosBetaInviteStore, InMemoryBetaInviteStore>(configuration);
        return services;
    }

    /// <summary><c>POST /admin/invites</c> with the <c>X-Admin-Key</c> header creates a code (default: one use, valid for 14 days).</summary>
    public static IEndpointRouteBuilder MapDialedBetaInvites(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/admin/invites", async (HttpContext http, CreateInviteRequest request, IBetaInviteStore store, IOptions<BetaOptions> options, TimeProvider time) =>
        {
            if (AdminKey.Check(http, options.Value) is { } refused)
            {
                return refused;
            }

            var uses = Math.Clamp(request.MaxUses ?? 1, 1, 1000);
            var days = Math.Clamp(request.DaysValid ?? 14, 1, 365);
            var code = string.IsNullOrWhiteSpace(request.Code) ? NewCode() : request.Code.Trim().ToUpperInvariant();
            var invite = new BetaInvite(code, uses, 0, time.GetUtcNow().AddDays(days));
            await store.AddAsync(invite, http.RequestAborted);
            return Results.Ok(new { invite.Code, invite.MaxUses, invite.ExpiresAt });
        });

        return endpoints;
    }

    private static string NewCode()
    {
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }
}
