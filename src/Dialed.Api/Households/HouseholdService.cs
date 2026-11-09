using System.Security.Cryptography;
using Dialed.Api.Auth;

namespace Dialed.Api.Households;

public sealed record Household(string Id, string Name, string OwnerId);

public sealed record Invite(string Code, string HouseholdId, DateTimeOffset ExpiresAt);

public sealed record MemberDto(string UserId, string DisplayName, bool IsOwner);

public sealed record HouseholdDto(string Id, string Name, IReadOnlyList<MemberDto> Members, bool YouAreOwner);

public sealed record InviteDto(string Code, DateTimeOffset ExpiresAt);

public sealed record JoinRequest(string Code);

public sealed record TransferRequest(string UserId);

public enum HouseholdOutcome
{
    Ok,
    NotFound,

    /// <summary>The action is for the household owner.</summary>
    NotOwner,

    /// <summary>The invite code is unknown, used or expired.</summary>
    InvalidInvite,

    /// <summary>The state of the household does not allow it (for example the owner leaving a household with other members); see the message.</summary>
    Conflict,
}

public sealed record HouseholdResult<T>(HouseholdOutcome Outcome, T? Value = default, string? Message = null)
{
    public bool Succeeded => Outcome == HouseholdOutcome.Ok;
}

/// <summary>Where households and invites are kept. In memory for now; Cosmos DB replaces it.</summary>
public interface IHouseholdStore
{
    Task<Household?> FindAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Returns the household, creating it with this owner when it does not exist yet.</summary>
    Task<Household> EnsureAsync(string id, string name, string ownerId, CancellationToken cancellationToken = default);

    Task SaveAsync(Household household, CancellationToken cancellationToken = default);

    /// <summary>Deletes the household and its open invites.</summary>
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    Task AddInviteAsync(Invite invite, CancellationToken cancellationToken = default);

    /// <summary>Takes the invite: it can be used once. <c>null</c> when unknown, used or expired.</summary>
    Task<Invite?> TakeInviteAsync(string code, DateTimeOffset now, CancellationToken cancellationToken = default);
}

public sealed class InMemoryHouseholdStore(IEnumerable<Household>? initial = null) : IHouseholdStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Household> households = (initial ?? []).ToDictionary(h => h.Id, StringComparer.Ordinal);
    private readonly Dictionary<string, Invite> invites = new(StringComparer.Ordinal);

    public Task<Household?> FindAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(households.GetValueOrDefault(id));
        }
    }

    public Task<Household> EnsureAsync(string id, string name, string ownerId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (!households.TryGetValue(id, out var household))
            {
                household = new Household(id, name, ownerId);
                households[id] = household;
            }

            return Task.FromResult(household);
        }
    }

    public Task SaveAsync(Household household, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            households[household.Id] = household;
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            households.Remove(id);
            foreach (var code in invites.Where(i => i.Value.HouseholdId == id).Select(i => i.Key).ToList())
            {
                invites.Remove(code);
            }
        }

        return Task.CompletedTask;
    }

    public Task AddInviteAsync(Invite invite, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            invites[invite.Code] = invite;
        }

        return Task.CompletedTask;
    }

    public Task<Invite?> TakeInviteAsync(string code, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            foreach (var expired in invites.Where(i => i.Value.ExpiresAt <= now).Select(i => i.Key).ToList())
            {
                invites.Remove(expired);
            }

            return Task.FromResult(invites.Remove(code, out var invite) ? invite : null);
        }
    }
}

/// <summary>
/// Households: everyone starts in a personal one, an owner can invite people with a single-use code, and members can leave or be removed
/// (back to a personal household of their own). Records stay in the household they were made in.
/// </summary>
public sealed class HouseholdService(IUserDirectory users, IHouseholdStore store, TimeProvider time)
{
    public const int MaxMembers = 10;
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I
    private static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);

    public static string PersonalHouseholdId(string userId) => "h-" + userId;

    public async Task<HouseholdResult<HouseholdDto>> GetMineAsync(string userId, CancellationToken ct = default)
    {
        if (await users.FindByIdAsync(userId, ct) is not { } user)
        {
            return new(HouseholdOutcome.NotFound);
        }

        var household = await EnsureAsync(user, ct);
        return new(HouseholdOutcome.Ok, await ToDtoAsync(household, userId, ct));
    }

    public async Task<HouseholdResult<InviteDto>> CreateInviteAsync(string userId, CancellationToken ct = default)
    {
        if (await users.FindByIdAsync(userId, ct) is not { } user)
        {
            return new(HouseholdOutcome.NotFound);
        }

        var household = await EnsureAsync(user, ct);
        if (household.OwnerId != userId)
        {
            return new(HouseholdOutcome.NotOwner);
        }

        if ((await users.ListByHouseholdAsync(household.Id, ct)).Count >= MaxMembers)
        {
            return new(HouseholdOutcome.Conflict, Message: "The household is full.");
        }

        var invite = new Invite(NewCode(), household.Id, time.GetUtcNow().Add(InviteLifetime));
        await store.AddInviteAsync(invite, ct);
        return new(HouseholdOutcome.Ok, new InviteDto(invite.Code, invite.ExpiresAt));
    }

    /// <summary>Joins the household the code belongs to. Only someone who is alone in their own household can join; leave first otherwise.</summary>
    public async Task<HouseholdResult<HouseholdDto>> JoinAsync(string userId, string code, CancellationToken ct = default)
    {
        if (await users.FindByIdAsync(userId, ct) is not { } user)
        {
            return new(HouseholdOutcome.NotFound);
        }

        var current = await EnsureAsync(user, ct);
        if ((await users.ListByHouseholdAsync(current.Id, ct)).Count > 1)
        {
            return new(HouseholdOutcome.Conflict, Message: "Leave your current household first.");
        }

        var invite = await store.TakeInviteAsync(Normalize(code), time.GetUtcNow(), ct);
        if (invite is null || await store.FindAsync(invite.HouseholdId, ct) is not { } target)
        {
            return new(HouseholdOutcome.InvalidInvite);
        }

        if ((await users.ListByHouseholdAsync(target.Id, ct)).Count >= MaxMembers)
        {
            return new(HouseholdOutcome.Conflict, Message: "The household is full.");
        }

        await users.SetHouseholdAsync(userId, target.Id, ct);
        return new(HouseholdOutcome.Ok, await ToDtoAsync(target, userId, ct));
    }

    /// <summary>Leaves the household for a personal one. The owner cannot leave while others are in it.</summary>
    public async Task<HouseholdResult<HouseholdDto>> LeaveAsync(string userId, CancellationToken ct = default)
    {
        if (await users.FindByIdAsync(userId, ct) is not { } user)
        {
            return new(HouseholdOutcome.NotFound);
        }

        var household = await EnsureAsync(user, ct);
        var members = await users.ListByHouseholdAsync(household.Id, ct);
        if (members.Count <= 1)
        {
            return new(HouseholdOutcome.Conflict, Message: "You are already alone in your household.");
        }

        if (household.OwnerId == userId)
        {
            return new(HouseholdOutcome.Conflict, Message: "The owner cannot leave while others are in the household. Remove them first.");
        }

        return new(HouseholdOutcome.Ok, await MoveToPersonalAsync(user, ct));
    }

    /// <summary>The owner removes a member, who gets a personal household again.</summary>
    public async Task<HouseholdResult<HouseholdDto>> RemoveMemberAsync(string ownerId, string memberId, CancellationToken ct = default)
    {
        if (await users.FindByIdAsync(ownerId, ct) is not { } owner)
        {
            return new(HouseholdOutcome.NotFound);
        }

        var household = await EnsureAsync(owner, ct);
        if (household.OwnerId != ownerId)
        {
            return new(HouseholdOutcome.NotOwner);
        }

        if (memberId == ownerId)
        {
            return new(HouseholdOutcome.Conflict, Message: "The owner cannot remove themselves.");
        }

        if (await users.FindByIdAsync(memberId, ct) is not { } member || member.HouseholdId != household.Id)
        {
            return new(HouseholdOutcome.NotFound);
        }

        await MoveToPersonalAsync(member, ct);
        return new(HouseholdOutcome.Ok, await ToDtoAsync(household, ownerId, ct));
    }

    /// <summary>The owner hands the household to another member, who becomes its owner. The previous owner stays a member (and can leave).</summary>
    public async Task<HouseholdResult<HouseholdDto>> TransferOwnershipAsync(string ownerId, string newOwnerId, CancellationToken ct = default)
    {
        if (await users.FindByIdAsync(ownerId, ct) is not { } owner)
        {
            return new(HouseholdOutcome.NotFound);
        }

        var household = await EnsureAsync(owner, ct);
        if (household.OwnerId != ownerId)
        {
            return new(HouseholdOutcome.NotOwner);
        }

        if (newOwnerId == ownerId)
        {
            return new(HouseholdOutcome.Conflict, Message: "You already own this household.");
        }

        if (await users.FindByIdAsync(newOwnerId, ct) is not { } member || member.HouseholdId != household.Id)
        {
            return new(HouseholdOutcome.NotFound);
        }

        var transferred = household with { OwnerId = newOwnerId };
        await store.SaveAsync(transferred, ct);
        return new(HouseholdOutcome.Ok, await ToDtoAsync(transferred, ownerId, ct));
    }

    private async Task<HouseholdDto> MoveToPersonalAsync(AppUser user, CancellationToken ct)
    {
        var personal = await store.EnsureAsync(PersonalHouseholdId(user.Id), DefaultName(user), user.Id, ct);
        await users.SetHouseholdAsync(user.Id, personal.Id, ct);
        return await ToDtoAsync(personal, user.Id, ct);
    }

    private Task<Household> EnsureAsync(AppUser user, CancellationToken ct) =>
        // Personal households are created on first use; a shared one exists already, so its owner is not guessed from who asks.
        store.EnsureAsync(user.HouseholdId, DefaultName(user), user.Id, ct);

    private async Task<HouseholdDto> ToDtoAsync(Household household, string viewerId, CancellationToken ct)
    {
        var members = await users.ListByHouseholdAsync(household.Id, ct);
        return new HouseholdDto(
            household.Id,
            household.Name,
            [.. members.Select(m => new MemberDto(m.Id, m.DisplayName, m.Id == household.OwnerId))],
            household.OwnerId == viewerId);
    }

    private static string DefaultName(AppUser user) => $"{user.DisplayName}'s household";

    private static string Normalize(string? code) => (code ?? "").Trim().ToUpperInvariant().Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);

    private static string NewCode()
    {
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        }

        return new string(chars);
    }
}
