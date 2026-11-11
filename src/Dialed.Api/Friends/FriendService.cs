using System.Security.Cryptography;
using Dialed.Api.Auth;
using Dialed.Api.Sync;

namespace Dialed.Api.Friends;

public enum FollowStatus { Pending, Accepted }

/// <summary>One person following another. Only accepted follows give access to what the followed person shares with friends.</summary>
public sealed record Follow(string FollowerId, string FolloweeId, FollowStatus Status, DateTimeOffset CreatedAt);

public sealed record PersonDto(string UserId, string DisplayName);

public sealed record FriendCodeDto(string Code);

public sealed record FollowRequest(string Code);

public sealed record FriendProfileDto(string UserId, string DisplayName, int Beans, int Recipes, int Brews, int Followers);

/// <summary>One line of the friends feed (#78): a brew or a new bean someone I follow shares with me.</summary>
/// <param name="Kind"><c>brew</c> or <c>bean</c>.</param>
/// <param name="BeanName">For a brew, the name of the bean it was made with (looked up in the friend's household), when known.</param>
public sealed record FeedEntryDto(string Kind, string OwnerId, string OwnerName, DateTimeOffset At, System.Text.Json.Nodes.JsonObject Item, string? BeanName);

public sealed record FeedPageDto(IReadOnlyList<FeedEntryDto> Entries, bool HasMore);
/// <summary>One shared bean or recipe with the names that make it understandable outside its household (#77).</summary>
public sealed record SharedItemDto(System.Text.Json.Nodes.JsonObject Item, string OwnerName, string? RoasterName, string? BeanName);

public sealed record SharedPageDto(IReadOnlyList<System.Text.Json.Nodes.JsonObject> Items, int Total, bool HasMore);

public enum FriendOutcome { Ok, NotFound, Conflict }

public sealed record FriendResult<T>(FriendOutcome Outcome, T? Value = default, string? Message = null);

/// <summary>Friend codes, follows and blocks. In memory for now; Cosmos DB replaces it.</summary>
public interface IFriendStore
{
    Task<string> GetOrCreateCodeAsync(string userId, Func<string> newCode, CancellationToken cancellationToken = default);

    Task<string> RotateCodeAsync(string userId, Func<string> newCode, CancellationToken cancellationToken = default);

    Task<string?> FindUserByCodeAsync(string code, CancellationToken cancellationToken = default);

    Task<Follow?> FindFollowAsync(string followerId, string followeeId, CancellationToken cancellationToken = default);

    Task SaveFollowAsync(Follow follow, CancellationToken cancellationToken = default);

    Task<bool> RemoveFollowAsync(string followerId, string followeeId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Follow>> FollowsAsync(string userId, bool asFollowee, CancellationToken cancellationToken = default);

    Task<bool> AddBlockAsync(string blockerId, string blockedId, CancellationToken cancellationToken = default);

    Task<bool> RemoveBlockAsync(string blockerId, string blockedId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> BlockedByAsync(string blockerId, CancellationToken cancellationToken = default);

    Task<bool> IsBlockedAsync(string blockerId, string blockedId, CancellationToken cancellationToken = default);

    /// <summary>Forgets everything about the person: friend code, follows in both directions and blocks in both directions.</summary>
    Task RemoveUserAsync(string userId, CancellationToken cancellationToken = default);
}

public sealed class InMemoryFriendStore : IFriendStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, string> codeByUser = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> userByCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Follower, string Followee), Follow> follows = [];
    private readonly HashSet<(string Blocker, string Blocked)> blocks = [];

    public Task<string> GetOrCreateCodeAsync(string userId, Func<string> newCode, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(codeByUser.TryGetValue(userId, out var code) ? code : Assign(userId, newCode));
        }
    }

    public Task<string> RotateCodeAsync(string userId, Func<string> newCode, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (codeByUser.TryGetValue(userId, out var old))
            {
                userByCode.Remove(old);
            }

            return Task.FromResult(Assign(userId, newCode));
        }
    }

    private string Assign(string userId, Func<string> newCode)
    {
        string code;
        do
        {
            code = newCode();
        }
        while (userByCode.ContainsKey(code));

        codeByUser[userId] = code;
        userByCode[code] = userId;
        return code;
    }

    public Task<string?> FindUserByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(userByCode.GetValueOrDefault(code.Trim()));
        }
    }

    public Task<Follow?> FindFollowAsync(string followerId, string followeeId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(follows.GetValueOrDefault((followerId, followeeId)));
        }
    }

    public Task SaveFollowAsync(Follow follow, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            follows[(follow.FollowerId, follow.FolloweeId)] = follow;
        }

        return Task.CompletedTask;
    }

    public Task<bool> RemoveFollowAsync(string followerId, string followeeId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(follows.Remove((followerId, followeeId)));
        }
    }

    public Task<IReadOnlyList<Follow>> FollowsAsync(string userId, bool asFollowee, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<Follow>>([.. follows.Values
                .Where(f => (asFollowee ? f.FolloweeId : f.FollowerId) == userId)
                .OrderBy(f => f.CreatedAt)]);
        }
    }

    public Task<bool> AddBlockAsync(string blockerId, string blockedId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(blocks.Add((blockerId, blockedId)));
        }
    }

    public Task<bool> RemoveBlockAsync(string blockerId, string blockedId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(blocks.Remove((blockerId, blockedId)));
        }
    }

    public Task<IReadOnlyList<string>> BlockedByAsync(string blockerId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<string>>([.. blocks.Where(b => b.Blocker == blockerId).Select(b => b.Blocked)]);
        }
    }

    public Task RemoveUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (codeByUser.Remove(userId, out var code))
            {
                userByCode.Remove(code);
            }

            foreach (var key in follows.Keys.Where(k => k.Follower == userId || k.Followee == userId).ToList())
            {
                follows.Remove(key);
            }

            blocks.RemoveWhere(b => b.Blocker == userId || b.Blocked == userId);
        }

        return Task.CompletedTask;
    }

    public Task<bool> IsBlockedAsync(string blockerId, string blockedId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(blocks.Contains((blockerId, blockedId)));
        }
    }
}

/// <summary>
/// Following with approval. There is no search for people: you follow someone by their friend code, they accept or decline, and
/// either side can end it. A blocked person cannot ask again and never learns that they are blocked.
/// </summary>
public sealed class FriendService(IUserDirectory users, IFriendStore store, ISyncStore sync, TimeProvider time, Push.INotifier notifier)
{
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private static string NewCode()
    {
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        }

        return new string(chars);
    }

    public async Task<FriendCodeDto> GetCodeAsync(string userId, CancellationToken ct = default) =>
        new(await store.GetOrCreateCodeAsync(userId, NewCode, ct));

    /// <summary>A new code; the old one stops working (for a code that was shared too widely).</summary>
    public async Task<FriendCodeDto> RotateCodeAsync(string userId, CancellationToken ct = default) =>
        new(await store.RotateCodeAsync(userId, NewCode, ct));

    public async Task<FriendResult<PersonDto>> FollowAsync(string userId, string code, CancellationToken ct = default)
    {
        var targetId = string.IsNullOrWhiteSpace(code) ? null : await store.FindUserByCodeAsync(code, ct);
        if (targetId is null || await users.FindByIdAsync(targetId, ct) is not { } target)
        {
            return new(FriendOutcome.NotFound, Message: "That friend code is not valid.");
        }

        // Blocking works both ways and looks like an unknown code.
        if (await store.IsBlockedAsync(targetId, userId, ct) || await store.IsBlockedAsync(userId, targetId, ct))
        {
            return new(FriendOutcome.NotFound, Message: "That friend code is not valid.");
        }

        if (targetId == userId)
        {
            return new(FriendOutcome.Conflict, Message: "That is your own friend code.");
        }

        if (await store.FindFollowAsync(userId, targetId, ct) is not null)
        {
            return new(FriendOutcome.Conflict, Message: "You already follow this person or asked to.");
        }

        await store.SaveFollowAsync(new Follow(userId, targetId, FollowStatus.Pending, time.GetUtcNow()), ct);
        notifier.FollowRequested(userId, targetId);
        return new(FriendOutcome.Ok, new PersonDto(target.Id, target.DisplayName));
    }

    public Task<IReadOnlyList<PersonDto>> RequestsAsync(string userId, CancellationToken ct = default) =>
        PeopleAsync(userId, asFollowee: true, FollowStatus.Pending, ct);

    public Task<IReadOnlyList<PersonDto>> FollowersAsync(string userId, CancellationToken ct = default) =>
        PeopleAsync(userId, asFollowee: true, FollowStatus.Accepted, ct);

    /// <summary>People you follow, plus the ones you asked that have not answered yet (<see cref="PendingFollowingAsync"/>).</summary>
    public Task<IReadOnlyList<PersonDto>> FollowingAsync(string userId, CancellationToken ct = default) =>
        PeopleAsync(userId, asFollowee: false, FollowStatus.Accepted, ct);

    public Task<IReadOnlyList<PersonDto>> PendingFollowingAsync(string userId, CancellationToken ct = default) =>
        PeopleAsync(userId, asFollowee: false, FollowStatus.Pending, ct);

    private async Task<IReadOnlyList<PersonDto>> PeopleAsync(string userId, bool asFollowee, FollowStatus status, CancellationToken ct)
    {
        List<PersonDto> people = [];
        foreach (var follow in await store.FollowsAsync(userId, asFollowee, ct))
        {
            var otherId = asFollowee ? follow.FollowerId : follow.FolloweeId;
            if (follow.Status == status && await users.FindByIdAsync(otherId, ct) is { } other)
            {
                people.Add(new PersonDto(other.Id, other.DisplayName));
            }
        }

        return people;
    }

    public async Task<FriendResult<PersonDto>> AcceptAsync(string userId, string followerId, CancellationToken ct = default)
    {
        if (await store.FindFollowAsync(followerId, userId, ct) is not { Status: FollowStatus.Pending } pending
            || await users.FindByIdAsync(followerId, ct) is not { } follower)
        {
            return new(FriendOutcome.NotFound);
        }

        await store.SaveFollowAsync(pending with { Status = FollowStatus.Accepted }, ct);
        notifier.FollowAccepted(userId, followerId);
        return new(FriendOutcome.Ok, new PersonDto(follower.Id, follower.DisplayName));
    }

    /// <summary>Declines a request. The asker is not told; a decline looks like no answer.</summary>
    public async Task<bool> DeclineAsync(string userId, string followerId, CancellationToken ct = default) =>
        await store.FindFollowAsync(followerId, userId, ct) is { Status: FollowStatus.Pending }
        && await store.RemoveFollowAsync(followerId, userId, ct);

    public Task<bool> UnfollowAsync(string userId, string followeeId, CancellationToken ct = default) => store.RemoveFollowAsync(userId, followeeId, ct);

    public Task<bool> RemoveFollowerAsync(string userId, string followerId, CancellationToken ct = default) => store.RemoveFollowAsync(followerId, userId, ct);

    /// <summary>Blocks someone: any follow in either direction ends, and they can neither ask again nor see anything.</summary>
    public async Task<bool> BlockAsync(string userId, string otherId, CancellationToken ct = default)
    {
        if (otherId == userId || await users.FindByIdAsync(otherId, ct) is null)
        {
            return false;
        }

        await store.RemoveFollowAsync(userId, otherId, ct);
        await store.RemoveFollowAsync(otherId, userId, ct);
        await store.AddBlockAsync(userId, otherId, ct);
        return true;
    }

    public Task<bool> UnblockAsync(string userId, string otherId, CancellationToken ct = default) => store.RemoveBlockAsync(userId, otherId, ct);

    public async Task<IReadOnlyList<PersonDto>> BlockedAsync(string userId, CancellationToken ct = default)
    {
        List<PersonDto> people = [];
        foreach (var id in await store.BlockedByAsync(userId, ct))
        {
            if (await users.FindByIdAsync(id, ct) is { } other)
            {
                people.Add(new PersonDto(other.Id, other.DisplayName));
            }
        }

        return people;
    }

    /// <summary>The tables a friend can browse.</summary>
    public static readonly string[] SharedTables = ["beans", "recipes", "brews"];

    /// <summary>
    /// How the viewer stands to the owner for reading, per household of the owner: author, same household, accepted follower, blocked or stranger.
    /// </summary>
    private async Task<(AppUser? Owner, IReadOnlyList<SharedScope> Scopes)> ReaderAsync(string viewerId, string ownerId, CancellationToken ct)
    {
        if (await users.FindByIdAsync(ownerId, ct) is not { } owner || await users.FindByIdAsync(viewerId, ct) is not { } viewer)
        {
            return (null, []);
        }

        var relationship = await RelationshipAsync(viewerId, ownerId, ct);
        var scopes = owner.AllHouseholdIds
            .Select(h => new SharedScope(h, relationship == Relationship.Stranger && viewer.IsMemberOf(h) ? Relationship.HouseholdMember : relationship))
            .ToList();
        return (owner, scopes);
    }

    private static bool MayBrowse(Relationship relationship) => relationship is Relationship.Author or Relationship.HouseholdMember or Relationship.AcceptedFollower;

    private static bool MayBrowse(IReadOnlyList<SharedScope> scopes) => scopes.Any(s => MayBrowse(s.Reader));

    /// <summary>Header of a friend's profile with counts of what is shared with the viewer. Strangers and blocked people get nothing, not even a hint the person exists.</summary>
    public async Task<FriendProfileDto?> ProfileAsync(string viewerId, string ownerId, CancellationToken ct = default)
    {
        var (owner, scopes) = await ReaderAsync(viewerId, ownerId, ct);
        if (owner is null || !MayBrowse(scopes))
        {
            return null;
        }

        var counts = new Dictionary<string, int>();
        foreach (var table in SharedTables)
        {
            counts[table] = (await sync.ListSharedAsync(scopes, ownerId, table, 0, 1, ct)).Total;
        }

        var followers = (await store.FollowsAsync(ownerId, asFollowee: true, ct)).Count(f => f.Status == FollowStatus.Accepted);
        return new FriendProfileDto(owner.Id, owner.DisplayName, counts["beans"], counts["recipes"], counts["brews"], followers);
    }

    /// <summary>One page of what the owner shares with the viewer, newest first. <c>null</c> when the viewer may not browse or the table is not shareable.</summary>
    public async Task<SharedPageDto?> SharedAsync(string viewerId, string ownerId, string table, int skip, int pageSize, CancellationToken ct = default)
    {
        if (!SharedTables.Contains(table, StringComparer.Ordinal))
        {
            return null;
        }

        var (owner, scopes) = await ReaderAsync(viewerId, ownerId, ct);
        if (owner is null || !MayBrowse(scopes))
        {
            return null;
        }

        skip = Math.Max(0, skip);
        pageSize = Math.Clamp(pageSize, 1, 50);
        var (rows, total) = await sync.ListSharedAsync(scopes, ownerId, table, skip, pageSize, ct);
        return new SharedPageDto([.. rows.Select(r => r.Entity)], total, skip + rows.Count < total);
    }

    /// <summary>Most rows read per person and table: the feed shows the latest activity, not all of history.</summary>
    private const int FeedDepth = 100;

    /// <summary>
    /// What the people I follow share with me, newest first: their brews and the beans they added (#78). Built when it is asked for
    /// (ADR-0013): the latest rows of each accepted friend are merged and cut to the page. Blocked or removed friends drop out because
    /// the same relationship rules as everywhere else decide what is readable.
    /// </summary>
    public async Task<FeedPageDto> FeedAsync(string viewerId, int skip, int pageSize, CancellationToken ct = default)
    {
        skip = Math.Clamp(skip, 0, FeedDepth - 1);
        pageSize = Math.Clamp(pageSize, 1, 50);
        var wanted = Math.Min(skip + pageSize + 1, FeedDepth);

        List<FeedEntryDto> entries = [];
        foreach (var follow in (await store.FollowsAsync(viewerId, asFollowee: false, ct)).Where(f => f.Status == FollowStatus.Accepted))
        {
            var (owner, scopes) = await ReaderAsync(viewerId, follow.FolloweeId, ct);
            if (owner is null || !MayBrowse(scopes))
            {
                continue;
            }

            foreach (var (table, kind) in new[] { ("brews", "brew"), ("beans", "bean") })
            {
                var (rows, _) = await sync.ListSharedAsync(scopes, owner.Id, table, 0, wanted, ct);
                foreach (var row in rows)
                {
                    var at = row.Entity[kind == "brew" ? "brewedAt" : "createdAt"]?.GetValue<DateTimeOffset>() ?? row.UpdatedAt;
                    entries.Add(new FeedEntryDto(kind, owner.Id, owner.DisplayName, at, (System.Text.Json.Nodes.JsonObject)row.Entity.DeepClone(), kind == "brew" ? await BeanNameOfBrewAsync(row, ct) : null));
                }
            }
        }

        var ordered = entries.OrderByDescending(e => e.At).ThenBy(e => e.Item["id"]?.GetValue<string>(), StringComparer.Ordinal).ToList();
        return new FeedPageDto([.. ordered.Skip(skip).Take(pageSize)], ordered.Count > skip + pageSize);
    }

    /// <summary>The bean a brew was made with: brew to bag to bean, in the household that holds them.</summary>
    private async Task<string?> BeanNameOfBrewAsync(SyncRow brew, CancellationToken ct)
    {
        if (brew.Entity["bagId"]?.GetValue<string>() is not { Length: > 0 } bagId
            || await sync.GetAsync(brew.HouseholdId, "bags", bagId, ct) is not { } bag
            || bag.Entity["beanId"]?.GetValue<string>() is not { Length: > 0 } beanId)
        {
            return null;
        }

        return (await sync.GetAsync(brew.HouseholdId, "beans", beanId, ct))?.Entity["name"]?.GetValue<string>();
    }

    /// <summary>
    /// One bean or recipe the owner shares with the viewer, to copy it (#77). The roaster's or bean's name comes along, because the
    /// vendor and bean records live in the owner's household. <c>null</c> when it does not exist or the viewer may not see it.
    /// </summary>
    public async Task<SharedItemDto?> SharedItemAsync(string viewerId, string ownerId, string table, string id, CancellationToken ct = default)
    {
        if (table is not ("beans" or "recipes"))
        {
            return null;
        }

        var (owner, scopes) = await ReaderAsync(viewerId, ownerId, ct);
        if (owner is null || !MayBrowse(scopes))
        {
            return null;
        }

        foreach (var scope in scopes)
        {
            if (await sync.GetAsync(scope.HouseholdId, table, id, ct) is not { } row || !IsShared(row.Entity, ownerId, scope.Reader))
            {
                continue;
            }

            string? roaster = null;
            string? bean = null;
            if (table == "beans" && row.Entity["roasterId"]?.GetValue<string>() is { Length: > 0 } roasterId)
            {
                roaster = (await sync.GetAsync(scope.HouseholdId, "vendors", roasterId, ct))?.Entity["name"]?.GetValue<string>();
            }
            else if (table == "recipes" && row.Entity["beanId"]?.GetValue<string>() is { Length: > 0 } beanId
                && await sync.GetAsync(scope.HouseholdId, "beans", beanId, ct) is { } beanRow && IsShared(beanRow.Entity, ownerId, scope.Reader))
            {
                bean = beanRow.Entity["name"]?.GetValue<string>();
            }

            return new SharedItemDto((System.Text.Json.Nodes.JsonObject)row.Entity.DeepClone(), owner.DisplayName, roaster, bean);
        }

        return null;
    }

    private static bool IsShared(System.Text.Json.Nodes.JsonObject entity, string ownerId, Relationship reader) =>
        entity["createdBy"]?.GetValue<string>() == ownerId
        && !(entity["deleted"] is System.Text.Json.Nodes.JsonValue d && d.TryGetValue<bool>(out var gone) && gone)
        && VisibilityRules.CanRead(VisibilityRules.Of(entity), reader);

    /// <summary>How <paramref name="viewerId"/> relates to <paramref name="ownerId"/>, for <see cref="VisibilityRules"/>.</summary>
    public async Task<Relationship> RelationshipAsync(string viewerId, string ownerId, CancellationToken ct = default)
    {
        if (viewerId == ownerId)
        {
            return Relationship.Author;
        }

        if (await store.IsBlockedAsync(ownerId, viewerId, ct) || await store.IsBlockedAsync(viewerId, ownerId, ct))
        {
            return Relationship.Blocked;
        }

        return await store.FindFollowAsync(viewerId, ownerId, ct) is { Status: FollowStatus.Accepted } ? Relationship.AcceptedFollower : Relationship.Stranger;
    }
}
