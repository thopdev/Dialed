namespace Dialed.Api.Auth;

/// <summary>Finds users. In memory for now; the Identity stores on Cosmos DB (ADR-0010) replace it when passkey sign-up lands.</summary>
public interface IUserDirectory
{
    Task<AppUser?> FindByIdAsync(string id, CancellationToken cancellationToken = default);

    Task AddAsync(AppUser user, CancellationToken cancellationToken = default);

    /// <summary>Everyone who belongs to the household, whichever household they are looking at right now.</summary>
    Task<IReadOnlyList<AppUser>> ListByHouseholdAsync(string householdId, CancellationToken cancellationToken = default);

    /// <summary>Removes the person. <c>false</c> when unknown.</summary>
    Task<bool> RemoveAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Makes the household the person's active one (joining or switching), remembering them as a member. Returns the updated user, or <c>null</c> when unknown.</summary>
    Task<AppUser?> SetHouseholdAsync(string userId, string householdId, CancellationToken cancellationToken = default);

    /// <summary>Ends the person's membership (leaving or being removed). When it was their active household another one of theirs becomes active; the only household cannot be left.</summary>
    Task<AppUser?> RemoveMembershipAsync(string userId, string householdId, CancellationToken cancellationToken = default);
}

public sealed class InMemoryUserDirectory : IUserDirectory
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, AppUser> users = new(StringComparer.Ordinal);

    public InMemoryUserDirectory(IEnumerable<AppUser>? initial = null)
    {
        foreach (var user in initial ?? [])
        {
            users[user.Id] = user;
        }
    }

    public Task<AppUser?> FindByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(users.GetValueOrDefault(id));
        }
    }

    public Task AddAsync(AppUser user, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            users[user.Id] = user;
        }

        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string userId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(users.Remove(userId));
        }
    }

    public Task<IReadOnlyList<AppUser>> ListByHouseholdAsync(string householdId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<AppUser>>([.. users.Values.Where(u => u.IsMemberOf(householdId)).OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)]);
        }
    }

    public Task<AppUser?> SetHouseholdAsync(string userId, string householdId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (!users.TryGetValue(userId, out var user))
            {
                return Task.FromResult<AppUser?>(null);
            }

            var others = user.HouseholdIds.Append(user.HouseholdId).Where(h => h != householdId).Distinct(StringComparer.Ordinal).ToList();
            var moved = user with { HouseholdId = householdId, HouseholdIds = others };
            users[userId] = moved;
            return Task.FromResult<AppUser?>(moved);
        }
    }

    public Task<AppUser?> RemoveMembershipAsync(string userId, string householdId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (!users.TryGetValue(userId, out var user))
            {
                return Task.FromResult<AppUser?>(null);
            }

            // Leaving the active household makes the first other one active; leaving the only one is not possible.
            var left = user.HouseholdId == householdId
                ? user.HouseholdIds.Count == 0 ? user : user with { HouseholdId = user.HouseholdIds[0], HouseholdIds = [.. user.HouseholdIds.Skip(1)] }
                : user with { HouseholdIds = [.. user.HouseholdIds.Where(h => h != householdId)] };
            users[userId] = left;
            return Task.FromResult<AppUser?>(left);
        }
    }
}

/// <summary>The test users that <c>POST /auth/dev</c> signs in as (Development only). Alice and Bob share a household; Carol has her own.</summary>
public static class DevUsers
{
    public static IReadOnlyList<AppUser> All { get; } =
    [
        new("dev-alice", "alice@dialed.test", "Alice", "dev-household"),
        new("dev-bob", "bob@dialed.test", "Bob", "dev-household"),
        new("dev-carol", "carol@dialed.test", "Carol", "dev-household-carol"),
    ];

    public static AppUser? Find(string name) =>
        All.FirstOrDefault(u => string.Equals(u.DisplayName, name, StringComparison.OrdinalIgnoreCase) || u.Id == name);
}
