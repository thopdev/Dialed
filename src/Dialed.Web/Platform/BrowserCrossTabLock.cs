using Dialed.Client.Platform;
using Microsoft.JSInterop;

namespace Dialed.Web.Platform;

/// <summary>Web Locks API (<c>navigator.locks</c>); the browser frees a lock when its tab closes. See <c>crossTabLock*</c> in platform.js.</summary>
internal sealed class BrowserCrossTabLock(BrowserModule browser) : ICrossTabLock
{
    private int nextId;

    public Task<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken cancellationToken = default) =>
        RequestAsync(name, wait: false, cancellationToken);

    public async Task<IAsyncDisposable> AcquireAsync(string name, CancellationToken cancellationToken = default) =>
        await RequestAsync(name, wait: true, cancellationToken) ?? throw new InvalidOperationException("The lock was not granted.");

    private async Task<IAsyncDisposable?> RequestAsync(string name, bool wait, CancellationToken cancellationToken)
    {
        var module = await browser.GetAsync();
        var id = Interlocked.Increment(ref nextId);
        try
        {
            return await module.InvokeAsync<bool>("crossTabLockAcquire", cancellationToken, id, name, wait) ? new Held(module, id) : null;
        }
        catch (JSException)
        {
            // Web Locks failed (for example a security error): behave as a single tab rather than never syncing.
            return NotLocked.Instance;
        }
        catch (OperationCanceledException)
        {
            // Stop waiting in the browser too (or free the lock if it was granted just now).
            await Release(module, id);
            throw;
        }
    }

    private static async Task Release(IJSObjectReference module, int id)
    {
        try
        {
            await module.InvokeVoidAsync("crossTabLockRelease", id);
        }
        catch (JSDisconnectedException)
        {
            // The tab is closing; the browser frees the lock itself.
        }
    }

    private sealed class NotLocked : IAsyncDisposable
    {
        public static readonly NotLocked Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Held(IJSObjectReference module, int id) : IAsyncDisposable
    {
        private int released;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                await Release(module, id);
            }
        }
    }
}
