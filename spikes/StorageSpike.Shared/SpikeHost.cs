using Microsoft.JSInterop;

namespace StorageSpike;

/// <summary>Implemented by each spike host: which library it uses and how to get the database ready.</summary>
public interface ISpikeHost
{
    string LibraryName { get; }

    /// <summary>Runs migrations / starts the storage worker. Must be idempotent.</summary>
    Task EnsureReadyAsync();
}

public sealed record StorageStatus(bool Persisted, long UsageBytes, long QuotaBytes, bool LocksSupported, bool Standalone, string UserAgent);

/// <summary>Browser storage facts that decide whether offline data survives on iOS.</summary>
public sealed class BrowserStorage(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? module;

    public async Task<bool> RequestPersistenceAsync() => await (await Module()).InvokeAsync<bool>("persist");

    public async Task<StorageStatus> GetStatusAsync() => await (await Module()).InvokeAsync<StorageStatus>("status");

    /// <summary>True when this tab got an exclusive Web Lock (another tab holding it returns false).</summary>
    public async Task<bool> TryExclusiveLockAsync(string name) => await (await Module()).InvokeAsync<bool>("tryLock", name);

    private async Task<IJSObjectReference> Module() =>
        module ??= await js.InvokeAsync<IJSObjectReference>("import", "./_content/StorageSpike.Shared/browserStorage.js");

    public async ValueTask DisposeAsync()
    {
        if (module is not null)
        {
            await module.DisposeAsync();
        }
    }
}
