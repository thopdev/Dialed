using System.Security.Cryptography;
using Dialed.Api.Auth;
using Dialed.Api.Sync;

namespace Dialed.Api.Households;

public sealed record Household(string Id, string Name, string OwnerId);

/// <param name="MaxUses">How many people may join with this code (1 to 10).</param>
public sealed record Invite(string Code, string HouseholdId, DateTimeOffset ExpiresAt, int MaxUses = 1, int Uses = 0);

public sealed record MemberDto(string UserId, string DisplayName, bool IsOwner);

public sealed record HouseholdDto(string Id, string Name, IReadOnlyList<MemberDto> Members, bool YouAreOwner);

public sealed record InviteDto(string Code, DateTimeOffset ExpiresAt, int MaxUses = 1, int Uses = 0);

/// <summary>Optional settings for a new invite; without them it is single-use and valid for 7 days.</summary>
public sealed record HouseholdInviteRequest(int? MaxUses, int? DaysValid);

/// <summary>One household the person belongs to, for the picker.</summary>
public sealed record HouseholdSummaryDto(string Id, string Name, bool IsPersonal, bool IsActive, bool IsOwner, int MemberCount);

public sealed record SwitchRequest(string HouseholdId);

public sealed record JoinRequest(string Code);

public sealed record TransferRequest(string UserId);

/// <summary>The name for a new household, or the new name of the active one.</summary>
public sealed record HouseholdNameRequest(string Name);

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

    /// <summary>The request itself is not acceptable (for example an empty name); see the message.</summary>
    Invalid,
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

    /// <summary>The invite as it stands, without using it. <c>null</c> when unknown, used up or expired.</summary>
    Task<Invite?> FindInviteAsync(string code, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Uses one use of the invite (it goes away with the last one). <c>null</c> when unknown, used up or expired.</summary>
    Task<Invite?> TakeInviteAsync(string code, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>The household's open invites, soonest to expire first.</summary>
    Task<IReadOnlyList<Invite>> ListInvitesAsync(string householdId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Withdraws an invite of this household. <c>false</c> when there is no such invite.</summary>
    Task<bool> RevokeInviteAsync(string householdId, string code, CancellationToken cancellationToken = default);
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

    public Task<Invite?> FindInviteAsync(string code, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            RemoveExpired(now);
            return Task.FromResult(invites.GetValueOrDefault(code));
        }
    }

    public Task<Invite?> TakeInviteAsync(string code, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            RemoveExpired(now);
            if (!invites.TryGetValue(code, out var invite))
            {
                return Task.FromResult<Invite?>(null);
            }

            var used = invite with { Uses = invite.Uses + 1 };
            if (used.Uses >= used.MaxUses)
            {
                invites.Remove(code);
            }
            else
            {
                invites[code] = used;
            }

            return Task.FromResult<Invite?>(used);
        }
    }

    public Task<IReadOnlyList<Invite>> ListInvitesAsync(string householdId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            RemoveExpired(now);
            return Task.FromResult<IReadOnlyList<Invite>>([.. invites.Values.Where(i => i.HouseholdId == householdId).OrderBy(i => i.ExpiresAt).ThenBy(i => i.Code, StringComparer.Ordinal)]);
        }
    }

    public Task<bool> RevokeInviteAsync(string householdId, string code, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(invites.TryGetValue(code, out var invite) && invite.HouseholdId == householdId && invites.Remove(code));
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var expired in invites.Where(i => i.Value.ExpiresAt <= now).Select(i => i.Key).ToList())
        {
            invites.Remove(expired);
        }
    }
}

/// <summary>
/// Households: everyone starts in a personal one, an owner can invite people with a single-use code, and members can leave or be removed
/// (back to a personal household of their own). Records stay in the household they were made in.
/// </summary>
public sealed class HouseholdService(IUserDirectory users, IHouseholdStore store, ISyncStore sync, TimeProvider time, Push.INotifier notifier)
{
    public const int MaxMembers = 10;

    /// <summary>How many households one person can create households up to; creating one more is refused.</summary>
    public const int MaxHouseholdsPerPerson = 10;

    public const int MaxNameLength = 60;
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

    /// <param name="maxUses">How many people may join with the code (1 to 10, default 1).</param>
    /// <param name="daysValid">1 to 30 days, default 7.</param>
    public async Task<HouseholdResult<InviteDto>> CreateInviteAsync(string userId, int? maxUses = null, int? daysValid = null, CancellationToken ct = default)
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

        var lifetime = daysValid is { } days ? TimeSpan.FromDays(Math.Clamp(days, 1, 30)) : InviteLifetime;
        var invite = new Invite(NewCode(), household.Id, time.GetUtcNow().Add(lifetime), Math.Clamp(maxUses ?? 1, 1, MaxMembers));
        await store.AddInviteAsync(invite, ct);
        return new(HouseholdOutcome.Ok, ToDto(invite));
    }

    /// <summary>The owner's open invites (a code stays open until it is used up, expires or is revoked).</summary>
    public async Task<HouseholdResult<IReadOnlyList<InviteDto>>> ListInvitesAsync(string userId, CancellationToken ct = default)
    {
        var (outcome, household) = await OwnedAsync(userId, ct);
        return outcome != HouseholdOutcome.Ok
            ? new(outcome)
            : new(HouseholdOutcome.Ok, [.. (await store.ListInvitesAsync(household!.Id, time.GetUtcNow(), ct)).Select(ToDto)]);
    }

    /// <summary>The owner withdraws an open invite.</summary>
    public async Task<HouseholdResult<bool>> RevokeInviteAsync(string userId, string code, CancellationToken ct = default)
    {
        var (outcome, household) = await OwnedAsync(userId, ct);
        if (outcome != HouseholdOutcome.Ok)
        {
            return new(outcome);
        }

        return await store.RevokeInviteAsync(household!.Id, Normalize(code), ct) ? new(HouseholdOutcome.Ok, true) : new(HouseholdOutcome.NotFound);
    }

    private async Task<(HouseholdOutcome Outcome, Household? Household)> OwnedAsync(string userId, CancellationToken ct)
    {
        if (await users.FindByIdAsync(userId, ct) is not { } user)
        {
            return (HouseholdOutcome.NotFound, null);
        }

        var household = await EnsureAsync(user, ct);
        return household.OwnerId == userId ? (HouseholdOutcome.Ok, household) : (HouseholdOutcome.NotOwner, null);
    }

    private static InviteDto ToDto(Invite invite) => new(invite.Code, invite.ExpiresAt, invite.MaxUses, invite.Uses);

    /// <summary>Joins the household the code belongs to and makes it the active one. Earlier households stay (switch back any time).</summary>
    public async Task<HouseholdResult<HouseholdDto>> JoinAsync(string userId, string code, CancellationToken ct = default)
    {
        if (await users.FindByIdAsync(userId, ct) is not { } user)
        {
            return new(HouseholdOutcome.NotFound);
        }

        // Look first, use the code last: a join that is refused (already a member, household full) must not burn a use.
        var normalized = Normalize(code);
        var invite = await store.FindInviteAsync(normalized, time.GetUtcNow(), ct);
        if (invite is null || await store.FindAsync(invite.HouseholdId, ct) is not { } target)
        {
            return new(HouseholdOutcome.InvalidInvite);
        }

        if (user.IsMemberOf(target.Id))
        {
            return new(HouseholdOutcome.Conflict, Message: "You are already in that household.");
        }

        if ((await users.ListByHouseholdAsync(target.Id, ct)).Count >= MaxMembers)
        {
            return new(HouseholdOutcome.Conflict, Message: "The household is full.");
        }

        // Two people can race for the last use: whoever takes it second is told the code is no longer valid.
        if (await store.TakeInviteAsync(normalized, time.GetUtcNow(), ct) is null)
        {
            return new(HouseholdOutcome.InvalidInvite);
        }

        await users.SetHouseholdAsync(userId, target.Id, ct);
        notifier.HouseholdJoined(userId, target.Id);
        return new(HouseholdOutcome.Ok, await ToDtoAsync(target, userId, ct));
    }

    /// <summary>Leaves the active household and goes back to the personal one. The personal household cannot be left, and the owner cannot leave while others are in it.</summary>
    public async Task<HouseholdResult<HouseholdDto>> LeaveAsync(string userId, CancellationToken ct = default)
    {
        if (await users.FindByIdAsync(userId, ct) is not { } user)
        {
            return new(HouseholdOutcome.NotFound);
        }

        var household = await EnsureAsync(user, ct);
        if (user.AllHouseholdIds.Count <= 1)
        {
            return new(HouseholdOutcome.Conflict, Message: "This is your only household; you cannot leave it.");
        }

        var members = await users.ListByHouseholdAsync(household.Id, ct);
        if (members.Count <= 1)
        {
            return new(HouseholdOutcome.Conflict, Message: "You are alone in this household; you cannot leave it.");
        }

        if (household.OwnerId == userId)
        {
            return new(HouseholdOutcome.Conflict, Message: "The owner cannot leave while others are in the household. Remove them first.");
        }

        return new(HouseholdOutcome.Ok, await EndMembershipAsync(user, household.Id, ct));
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

        if (await users.FindByIdAsync(memberId, ct) is not { } member || !member.IsMemberOf(household.Id))
        {
            return new(HouseholdOutcome.NotFound);
        }

        if (member.AllHouseholdIds.Count <= 1)
        {
            return new(HouseholdOutcome.Conflict, Message: "This is their only household; they cannot be removed from it.");
        }

        await EndMembershipAsync(member, household.Id, ct);
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

        if (await users.FindByIdAsync(newOwnerId, ct) is not { } member || !member.IsMemberOf(household.Id))
        {
            return new(HouseholdOutcome.NotFound);
        }

        var transferred = household with { OwnerId = newOwnerId };
        await store.SaveAsync(transferred, ct);
        return new(HouseholdOutcome.Ok, await ToDtoAsync(transferred, ownerId, ct));
    }

    /// <summary>Creates a household owned by the person and makes it the active one (the next token carries it). Earlier households stay.</summary>
    public async Task<HouseholdResult<HouseholdDto>> CreateAsync(string userId, string? name, CancellationToken ct = default)
    {
        if (await users.FindByIdAsync(userId, ct) is not { } user)
        {
            return new(HouseholdOutcome.NotFound);
        }

        if (CleanName(name) is not { } clean)
        {
            return new(HouseholdOutcome.Invalid, Message: $"Give the household a name of up to {MaxNameLength} characters.");
        }

        if (user.AllHouseholdIds.Count >= MaxHouseholdsPerPerson)
        {
            return new(HouseholdOutcome.Conflict, Message: $"You can be in up to {MaxHouseholdsPerPerson} households. Leave one first.");
        }

        var household = new Household("hh-" + Guid.NewGuid().ToString("N"), clean, userId);
        await store.SaveAsync(household, ct);
        await users.SetHouseholdAsync(userId, household.Id, ct);
        return new(HouseholdOutcome.Ok, await ToDtoAsync(household, userId, ct));
    }

    /// <summary>The owner renames the active household.</summary>
    public async Task<HouseholdResult<HouseholdDto>> RenameAsync(string userId, string? name, CancellationToken ct = default)
    {
        var (outcome, household) = await OwnedAsync(userId, ct);
        if (outcome != HouseholdOutcome.Ok)
        {
            return new(outcome);
        }

        if (CleanName(name) is not { } clean)
        {
            return new(HouseholdOutcome.Invalid, Message: $"Give the household a name of up to {MaxNameLength} characters.");
        }

        var renamed = household! with { Name = clean };
        await store.SaveAsync(renamed, ct);
        return new(HouseholdOutcome.Ok, await ToDtoAsync(renamed, userId, ct));
    }

    /// <summary>
    /// The owner deletes the active household with all its data, the way deleting an account removes a household its last member was alone in.
    /// Every member loses it (someone with no other household gets a personal one again), so their devices purge its records on the next sync.
    /// The owner's last household cannot be deleted, as it cannot be left. Returns the owner's household that is active now.
    /// </summary>
    public async Task<HouseholdResult<HouseholdDto>> DeleteAsync(string userId, CancellationToken ct = default)
    {
        var (outcome, household) = await OwnedAsync(userId, ct);
        if (outcome != HouseholdOutcome.Ok)
        {
            return new(outcome);
        }

        var owner = (await users.FindByIdAsync(userId, ct))!;
        if (owner.AllHouseholdIds.Count <= 1)
        {
            return new(HouseholdOutcome.Conflict, Message: "This is your only household; you cannot delete it.");
        }

        var members = await users.ListByHouseholdAsync(household!.Id, ct);

        // Someone whose only household this is falls back to their personal one; when that is this very household (it was handed to the owner),
        // they would come back to the same id and their devices would not notice it was emptied, so refuse instead.
        if (members.FirstOrDefault(m => m.Id != userId && m.AllHouseholdIds.Count <= 1 && m.PersonalHouseholdId == household.Id) is { } stuck)
        {
            return new(HouseholdOutcome.Conflict, Message: $"This household is the only one {stuck.DisplayName} has, so it cannot be deleted until they join or create another.");
        }

        await sync.DeleteHouseholdAsync(household.Id, ct);
        await store.DeleteAsync(household.Id, ct);
        foreach (var member in members.Where(m => m.Id != userId))
        {
            if (member.AllHouseholdIds.Count <= 1)
            {
                await store.EnsureAsync(member.PersonalHouseholdId, DefaultName(member), member.Id, ct);
                await users.SetHouseholdAsync(member.Id, member.PersonalHouseholdId, ct);
            }

            await users.RemoveMembershipAsync(member.Id, household.Id, ct);
        }

        return new(HouseholdOutcome.Ok, await EndMembershipAsync(owner, household.Id, ct));
    }

    /// <summary>A trimmed name of 1 to <see cref="MaxNameLength"/> characters, or <c>null</c> when there is none.</summary>
    private static string? CleanName(string? name) =>
        (name ?? "").Trim() is { Length: > 0 and <= MaxNameLength } clean ? clean : null;

    /// <summary>Every household the person belongs to, the active one marked.</summary>
    public async Task<HouseholdResult<IReadOnlyList<HouseholdSummaryDto>>> ListMineAsync(string userId, CancellationToken ct = default)
    {
        if (await users.FindByIdAsync(userId, ct) is not { } user)
        {
            return new(HouseholdOutcome.NotFound);
        }

        // A personal household exists from first use on.
        await EnsureAsync(user, ct);
        if (user.IsMemberOf(user.PersonalHouseholdId))
        {
            await store.EnsureAsync(user.PersonalHouseholdId, DefaultName(user), user.Id, ct);
        }

        var list = new List<HouseholdSummaryDto>();
        foreach (var id in user.AllHouseholdIds)
        {
            if (await store.FindAsync(id, ct) is not { } household)
            {
                continue;
            }

            list.Add(new HouseholdSummaryDto(
                household.Id,
                household.Name,
                household.Id == user.PersonalHouseholdId,
                household.Id == user.HouseholdId,
                household.OwnerId == userId,
                (await users.ListByHouseholdAsync(household.Id, ct)).Count));
        }

        return new(HouseholdOutcome.Ok, list);
    }

    /// <summary>Makes another household of the person the active one (the next token carries it).</summary>
    public async Task<HouseholdResult<HouseholdDto>> SwitchAsync(string userId, string householdId, CancellationToken ct = default)
    {
        if (await users.FindByIdAsync(userId, ct) is not { } user)
        {
            return new(HouseholdOutcome.NotFound);
        }

        if (!user.IsMemberOf(householdId))
        {
            return new(HouseholdOutcome.NotFound);
        }

        var target = householdId == user.PersonalHouseholdId
            ? await store.EnsureAsync(householdId, DefaultName(user), user.Id, ct)
            : await store.FindAsync(householdId, ct);
        if (target is null)
        {
            return new(HouseholdOutcome.NotFound);
        }

        await users.SetHouseholdAsync(userId, target.Id, ct);
        return new(HouseholdOutcome.Ok, await ToDtoAsync(target, userId, ct));
    }

    /// <summary>The person leaves (or is removed from) the household. When it was their active one another household of theirs becomes active; that one is returned.</summary>
    private async Task<HouseholdDto> EndMembershipAsync(AppUser user, string householdId, CancellationToken ct)
    {
        var left = await users.RemoveMembershipAsync(user.Id, householdId, ct) ?? user;
        var active = await store.EnsureAsync(left.HouseholdId, DefaultName(user), user.Id, ct);
        return await ToDtoAsync(active, user.Id, ct);
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
