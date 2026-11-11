using System.Security.Claims;
using System.Text.Json.Nodes;
using Dialed.Api.Auth;
using Dialed.Api.Friends;
using Dialed.Api.Households;
using Dialed.Api.Passkeys;
using Microsoft.AspNetCore.Identity;
using Dialed.Api.Sync;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Dialed.Api.Account;

public sealed record ExportProfile(string UserId, string DisplayName, string? Email, string HouseholdId, bool PublicProfile = false);

public sealed record AccountExport(
    int FormatVersion,
    DateTimeOffset ExportedAt,
    ExportProfile Profile,
    IReadOnlyDictionary<string, IReadOnlyList<JsonObject>> Items,
    IReadOnlyList<PersonDto> Following,
    IReadOnlyList<PersonDto> Followers);

/// <summary>Everything one person made, as one JSON document they can keep or move (#36). Photos are included as records that link to their files.</summary>
public sealed class AccountExporter(IUserDirectory users, ISyncStore sync, FriendService friends, PublicProfiles.IPublicProfileStore publicProfiles, TimeProvider time)
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
            var authored = new List<JsonObject>();
            foreach (var household in user.AllHouseholdIds)
            {
                authored.AddRange((await sync.ListAuthoredAsync(household, userId, table, ct)).Select(r => r.Entity));
            }

            items[table] = authored;
        }

        return new AccountExport(
            FormatVersion,
            time.GetUtcNow(),
            new ExportProfile(user.Id, user.DisplayName, user.Email, user.HouseholdId, (await publicProfiles.GetAsync(userId, ct)).Enabled),
            items,
            await friends.FollowingAsync(userId, ct),
            await friends.FollowersAsync(userId, ct));
    }
}

/// <summary>
/// Deletes a person (#35): the account and passkeys, sessions, friend codes, follows and blocks. A household they were alone in goes with all its data;
/// a shared one keeps its items (no longer pointing to the person) and passes to its longest-standing member when they owned it.
/// </summary>
public sealed class AccountDeleter(
    IUserDirectory directory,
    UserManager<AccountUser> accounts,
    IHouseholdStore households,
    IFriendStore friends,
    PublicProfiles.IPublicProfileStore publicProfiles,
    IRefreshTokenStore sessions,
    ISyncStore sync)
{
    public const string DeletedUser = "deleted-user";

    public async Task<bool> DeleteAsync(string userId, CancellationToken ct = default)
    {
        if (await directory.FindByIdAsync(userId, ct) is not { } user)
        {
            return false;
        }

        // Every household the person belongs to: the ones they were alone in go with their data, the shared ones keep their items.
        foreach (var householdId in user.AllHouseholdIds)
        {
            var members = await directory.ListByHouseholdAsync(householdId, ct);
            var others = members.Where(m => m.Id != userId).ToList();
            if (others.Count == 0)
            {
                await sync.DeleteHouseholdAsync(householdId, ct);
                await households.DeleteAsync(householdId, ct);
            }
            else
            {
                await sync.AnonymiseAuthorAsync(householdId, userId, DeletedUser, ct);
                if (await households.FindAsync(householdId, ct) is { } household && household.OwnerId == userId)
                {
                    await households.SaveAsync(household with { OwnerId = await LongestStandingAsync(others, ct) }, ct);
                }
            }
        }

        await friends.RemoveUserAsync(userId, ct);
        await publicProfiles.RemoveUserAsync(userId, ct);
        await sessions.RevokeUserAsync(userId, ct);
        if (await accounts.FindByIdAsync(userId) is { } account)
        {
            await accounts.DeleteAsync(account);
        }

        await directory.RemoveAsync(userId, ct);
        return true;
    }

    private async Task<string> LongestStandingAsync(IReadOnlyList<AppUser> members, CancellationToken ct)
    {
        // The oldest account first; people without a stored account (development users) come last, by name.
        var dated = new List<(AppUser Member, DateTimeOffset Created)>();
        foreach (var member in members)
        {
            var account = await accounts.FindByIdAsync(member.Id);
            dated.Add((member, account?.CreatedAt ?? DateTimeOffset.MaxValue));
        }

        _ = ct;
        return dated.OrderBy(d => d.Created).ThenBy(d => d.Member.DisplayName, StringComparer.OrdinalIgnoreCase).First().Member.Id;
    }
}

public static class AccountEndpoints
{
    public static IServiceCollection AddDialedAccount(this IServiceCollection services)
    {
        services.AddSingleton<AccountExporter>();
        services.AddScoped<AccountDeleter>();
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

        // Deleting needs a fresh passkey check, so a stolen access token alone cannot erase the account.
        group.MapPost("/delete/options", (HttpContext http, PasskeyCeremonies ceremonies) => ceremonies.ReauthOptionsAsync(http));
        group.MapPost("/delete", async (HttpContext http, CeremonyCompleteRequest request, ClaimsPrincipal user, PasskeyCeremonies ceremonies, AccountDeleter deleter) =>
        {
            var userId = user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "";
            if (await ceremonies.VerifyReauthAsync(http, request, userId) is { } refused)
            {
                return refused;
            }

            return await deleter.DeleteAsync(userId, http.RequestAborted) ? Results.NoContent() : Results.NotFound();
        });

        // Passkeys and recovery codes of the signed-in account (#112).
        group.MapGet("/passkeys", async (ClaimsPrincipal user, RecoveryService recovery, CancellationToken ct) =>
            await recovery.ListPasskeysAsync(Sub(user), ct) is { } list ? Results.Ok(list) : Results.NotFound());
        group.MapPut("/passkeys/{id}", async (string id, RenamePasskeyRequest request, ClaimsPrincipal user, RecoveryService recovery, CancellationToken ct) =>
            await recovery.RenamePasskeyAsync(Sub(user), id, request.Name, ct) ? Results.NoContent() : Results.NotFound());
        group.MapDelete("/passkeys/{id}", async (string id, ClaimsPrincipal user, RecoveryService recovery, CancellationToken ct) =>
            await recovery.RemovePasskeyAsync(Sub(user), id, ct) switch
            {
                RemoveResult.Removed => Results.NoContent(),
                RemoveResult.LastOne => Results.Problem(detail: "You cannot remove your last passkey.", statusCode: StatusCodes.Status409Conflict),
                _ => Results.NotFound(),
            });
        group.MapPost("/passkeys/options", (HttpContext http, ClaimsPrincipal user, PasskeyCeremonies ceremonies) => ceremonies.AddPasskeyOptionsAsync(http, Sub(user)));
        group.MapPost("/passkeys", (HttpContext http, CeremonyCompleteRequest request, ClaimsPrincipal user, PasskeyCeremonies ceremonies, string? name) => ceremonies.AddPasskeyAsync(http, request, Sub(user), name));
        group.MapGet("/recovery", async (ClaimsPrincipal user, RecoveryService recovery, CancellationToken ct) =>
            await recovery.StatusAsync(Sub(user), ct) is { } status ? Results.Ok(status) : Results.NotFound());
        group.MapPost("/recovery/codes", async (ClaimsPrincipal user, RecoveryService recovery, CancellationToken ct) =>
            await recovery.RegenerateCodesAsync(Sub(user), ct) is { } codes ? Results.Ok(new RecoveryCodesDto(codes)) : Results.NotFound());

        return endpoints;
    }

    private static string Sub(ClaimsPrincipal user) => user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "";
}
