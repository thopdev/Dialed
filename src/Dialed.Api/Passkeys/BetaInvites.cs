using System.Security.Cryptography;
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

public static class BetaInviteExtensions
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static IServiceCollection AddDialedBetaInvites(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BetaOptions>(configuration.GetSection(BetaOptions.SectionName));
        services.AddSingleton<IBetaInviteStore, InMemoryBetaInviteStore>();
        return services;
    }

    /// <summary><c>POST /admin/invites</c> with the <c>X-Admin-Key</c> header creates a code (default: one use, valid for 14 days).</summary>
    public static IEndpointRouteBuilder MapDialedBetaInvites(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/admin/invites", async (HttpContext http, CreateInviteRequest request, IBetaInviteStore store, IOptions<BetaOptions> options, TimeProvider time) =>
        {
            var key = options.Value.AdminKey;
            if (string.IsNullOrEmpty(key))
            {
                return Results.NotFound();
            }

            var given = http.Request.Headers["X-Admin-Key"].ToString();
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(given), System.Text.Encoding.UTF8.GetBytes(key)))
            {
                return Results.Unauthorized();
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
