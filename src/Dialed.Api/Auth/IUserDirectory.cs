namespace Dialed.Api.Auth;

/// <summary>Finds users. In memory for now; the Identity stores on Cosmos DB (ADR-0010) replace it when passkey sign-up lands.</summary>
public interface IUserDirectory
{
    Task<AppUser?> FindByIdAsync(string id, CancellationToken cancellationToken = default);

    Task AddAsync(AppUser user, CancellationToken cancellationToken = default);
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
