namespace Dialed.Client.Platform;

/// <summary>
/// A lock shared by every tab (or window) of the app on this device, so only one of them does a job at a time, such as syncing.
/// The platform frees it when the tab that holds it closes. Web: Web Locks API (<c>navigator.locks</c>); where that is missing every
/// tab gets the lock at once, which is how the app behaved before. Single-window platforms can use <see cref="SingleWindowLock"/>.
/// </summary>
public interface ICrossTabLock
{
    /// <summary>Takes the lock when it is free; <c>null</c> when another tab holds it. Dispose the result to free it.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Waits until no other tab holds the lock, then takes it. Dispose the result to free it.</summary>
    Task<IAsyncDisposable> AcquireAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>For a platform with one window (and for tests): the lock is always free.</summary>
public sealed class SingleWindowLock : ICrossTabLock
{
    public static readonly SingleWindowLock Instance = new();

    public Task<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult<IAsyncDisposable?>(Released.Instance);

    public Task<IAsyncDisposable> AcquireAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult<IAsyncDisposable>(Released.Instance);

    private sealed class Released : IAsyncDisposable
    {
        public static readonly Released Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
