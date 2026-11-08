namespace Dialed.Client.Account;

/// <summary>Where the sign-in is kept between launches. In memory by default; the host brings a persistent one.</summary>
public interface ISessionStore
{
    Task<SessionTokens?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(SessionTokens tokens, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed class InMemorySessionStore : ISessionStore
{
    private SessionTokens? tokens;

    public Task<SessionTokens?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(tokens);

    public Task SaveAsync(SessionTokens tokens, CancellationToken cancellationToken = default)
    {
        this.tokens = tokens;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        tokens = null;
        return Task.CompletedTask;
    }
}

/// <summary>Who is signed in on this device right now.</summary>
public sealed class AuthSession(ISessionStore store)
{
    public SessionTokens? Tokens { get; private set; }

    public bool IsSignedIn => Tokens is not null;

    public string? DisplayName => Tokens?.DisplayName;

    public event Action? Changed;

    /// <summary>Picks up a sign-in kept from an earlier launch. Call once at start-up.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Tokens = await store.LoadAsync(cancellationToken);
        Changed?.Invoke();
    }

    public async Task SetAsync(SessionTokens tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        Tokens = tokens;
        await store.SaveAsync(tokens, cancellationToken);
        Changed?.Invoke();
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        Tokens = null;
        await store.ClearAsync(cancellationToken);
        Changed?.Invoke();
    }
}
