using System.Security.Claims;
using System.Text.Json.Nodes;
using Dialed.Api.Auth;
using Dialed.Api.Friends;
using Dialed.Api.Sync;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Dialed.Api.Account;

public sealed record ExportProfile(string UserId, string DisplayName, string? Email, string HouseholdId);

public sealed record AccountExport(
    int FormatVersion,
    DateTimeOffset ExportedAt,
    ExportProfile Profile,
    IReadOnlyDictionary<string, IReadOnlyList<JsonObject>> Items,
    IReadOnlyList<PersonDto> Following,
    IReadOnlyList<PersonDto> Followers);

/// <summary>Everything one person made, as one JSON document they can keep or move (#36). Photos are included as records that link to their files.</summary>
public sealed class AccountExporter(IUserDirectory users, ISyncStore sync, FriendService friends, TimeProvider time)
{
    public const int FormatVersion = 1;

    public async Task<AccountExport?> ExportAsync(string userId, CancellationToken ct = default)
    {
        if (await users.FindByIdAsync(userId, ct) is not { } user)
        {
            return null;
        }

        var items = new Dictionary<string, IReadOnlyList<JsonObject>>(StringComparer.Ordinal);
        foreach (var table in SyncEndpoints.Tables.Order(StringComparer.Ordinal))
        {
            items[table] = [.. (await sync.ListAuthoredAsync(user.HouseholdId, userId, table, ct)).Select(r => r.Entity)];
        }

        return new AccountExport(
            FormatVersion,
            time.GetUtcNow(),
            new ExportProfile(user.Id, user.DisplayName, user.Email, user.HouseholdId),
            items,
            await friends.FollowingAsync(userId, ct),
            await friends.FollowersAsync(userId, ct));
    }
}

public static class AccountEndpoints
{
    public static IServiceCollection AddDialedAccount(this IServiceCollection services)
    {
        services.AddSingleton<AccountExporter>();
        return services;
    }

    public static IEndpointRouteBuilder MapDialedAccount(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/account").RequireAuthorization();

        // Sent as a file so the browser downloads it; the app can also share it.
        group.MapGet("/export", async (ClaimsPrincipal user, AccountExporter exporter, CancellationToken ct) =>
            await exporter.ExportAsync(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "", ct) is { } export
                ? Results.File(
                    System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(export, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true }),
                    "application/json",
                    "dialed-export.json")
                : Results.NotFound());

        return endpoints;
    }
}
