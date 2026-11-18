using Dialed.Api.Data;
using Microsoft.Azure.Cosmos;

namespace Dialed.Api.Auth;

/// <summary>Finds people (their profile and households). In memory for tests and a bare <c>dotnet run</c>, Cosmos DB otherwise.</summary>
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

    public InMemoryUserDirectory(UserDirectorySeed? seed = null)
    {
        foreach (var user in seed?.Users ?? [])
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

/// <summary>People every directory starts with: the <see cref="DevUsers"/> in Development, nobody elsewhere.</summary>
public sealed record UserDirectorySeed(IReadOnlyList<AppUser> Users);

/// <summary>
/// The directory in Cosmos DB: one <c>profile:{userId}</c> document per person in the <c>identity</c> container, so finding
/// someone is a point read. Changing households reads with the ETag and retries when another request was first.
/// Listing a household's members is a query over the <c>households</c> array (small: a household has a handful of people).
/// The seed users are created once per instance on first use, and never overwritten.
/// </summary>
public sealed class CosmosUserDirectory(DialedCosmos cosmos, UserDirectorySeed seed) : IUserDirectory
{
    private readonly Lock gate = new();
    private Task? seeded;

    public async Task<AppUser?> FindByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var container = await ContainerAsync(cancellationToken);
        var (document, _) = await container.ReadAsync<ProfileDocument>(ProfileDocument.IdFor(id), cancellationToken);
        return document?.ToUser();
    }

    public async Task AddAsync(AppUser user, CancellationToken cancellationToken = default)
    {
        var container = await ContainerAsync(cancellationToken);
        var document = ProfileDocument.From(user);
        await container.UpsertItemAsync(document, new PartitionKey(document.Pk), cancellationToken: cancellationToken);
    }

    public async Task<bool> RemoveAsync(string userId, CancellationToken cancellationToken = default)
    {
        var container = await ContainerAsync(cancellationToken);
        return await container.TryDeleteAsync(ProfileDocument.IdFor(userId), null, cancellationToken);
    }

    public async Task<IReadOnlyList<AppUser>> ListByHouseholdAsync(string householdId, CancellationToken cancellationToken = default)
    {
        var container = await ContainerAsync(cancellationToken);
        var query = new QueryDefinition("SELECT * FROM c WHERE c.type = 'profile' AND ARRAY_CONTAINS(c.households, @household)")
            .WithParameter("@household", householdId);
        var documents = await container.QueryAsync<ProfileDocument>(query, cancellationToken);
        return [.. documents.Select(d => d.ToUser()).OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    public Task<AppUser?> SetHouseholdAsync(string userId, string householdId, CancellationToken cancellationToken = default) =>
        ChangeAsync(userId, user =>
        {
            var others = user.HouseholdIds.Append(user.HouseholdId).Where(h => h != householdId).Distinct(StringComparer.Ordinal).ToList();
            return user with { HouseholdId = householdId, HouseholdIds = others };
        }, cancellationToken);

    public Task<AppUser?> RemoveMembershipAsync(string userId, string householdId, CancellationToken cancellationToken = default) =>
        ChangeAsync(userId, user => user.HouseholdId == householdId
            ? user.HouseholdIds.Count == 0 ? user : user with { HouseholdId = user.HouseholdIds[0], HouseholdIds = [.. user.HouseholdIds.Skip(1)] }
            : user with { HouseholdIds = [.. user.HouseholdIds.Where(h => h != householdId)] }, cancellationToken);

    private async Task<AppUser?> ChangeAsync(string userId, Func<AppUser, AppUser> change, CancellationToken cancellationToken)
    {
        var container = await ContainerAsync(cancellationToken);
        for (var attempt = 0; attempt < CosmosIdentity.MaxRetries; attempt++)
        {
            var (document, etag) = await container.ReadAsync<ProfileDocument>(ProfileDocument.IdFor(userId), cancellationToken);
            if (document is null)
            {
                return null;
            }

            var changed = change(document.ToUser());
            if (await container.TryWriteAsync(ProfileDocument.From(changed), etag, cancellationToken))
            {
                return changed;
            }
        }

        throw new InvalidOperationException("Could not change the household membership: too many concurrent changes.");
    }

    private async Task<Container> ContainerAsync(CancellationToken cancellationToken)
    {
        var container = await cosmos.ContainerAsync(cancellationToken);
        if (seed.Users.Count > 0)
        {
            Task task;
            lock (gate)
            {
                task = seeded is { IsFaulted: false } ? seeded : seeded = SeedAsync(container);
            }

            await task.WaitAsync(cancellationToken);
        }

        return container;
    }

    private async Task SeedAsync(Container container)
    {
        foreach (var user in seed.Users)
        {
            // Create only: a seed user who switched household keeps it.
            await container.TryWriteAsync(ProfileDocument.From(user), null, CancellationToken.None);
        }
    }

    internal sealed record ProfileDocument : IdentityDocument
    {
        public string UserId { get; init; } = "";
        public string? Email { get; init; }
        public string DisplayName { get; init; } = "";
        public string HouseholdId { get; init; } = "";
        public List<string> HouseholdIds { get; init; } = [];

        /// <summary>Every household, the active one included, for the members query.</summary>
        public List<string> Households { get; init; } = [];

        public static string IdFor(string userId) => "profile:" + userId;

        public static ProfileDocument From(AppUser user) => new()
        {
            Id = IdFor(user.Id),
            Type = "profile",
            UserId = user.Id,
            Email = user.Email,
            DisplayName = user.DisplayName,
            HouseholdId = user.HouseholdId,
            HouseholdIds = [.. user.HouseholdIds],
            Households = [.. user.AllHouseholdIds],
        };

        public AppUser ToUser() => new(UserId, Email, DisplayName, HouseholdId) { HouseholdIds = HouseholdIds };
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
