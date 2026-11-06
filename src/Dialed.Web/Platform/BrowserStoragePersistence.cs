using Dialed.Client.Platform;
using Microsoft.JSInterop;

namespace Dialed.Web.Platform;

internal sealed class BrowserStoragePersistence(BrowserModule browser) : IStoragePersistence
{
    public async Task<bool> IsPersistedAsync() =>
        await (await browser.GetAsync()).InvokeAsync<bool>("storagePersisted");

    public async Task<bool> RequestPersistenceAsync() =>
        await (await browser.GetAsync()).InvokeAsync<bool>("storagePersist");

    public async Task<StorageEstimate?> EstimateAsync()
    {
        var estimate = await (await browser.GetAsync()).InvokeAsync<EstimateResult?>("storageEstimate");
        return estimate is null ? null : new StorageEstimate(estimate.Usage, estimate.Quota);
    }

    private sealed record EstimateResult(long Usage, long Quota);
}
