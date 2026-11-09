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
public sealed class FriendService(IUserDirectory users, IFriendStore store, TimeProvider time)
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
