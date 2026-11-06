using Dialed.Client.Platform;
using Microsoft.JSInterop;

namespace Dialed.Web.Platform;

internal sealed class BrowserWakeLock(BrowserModule browser) : IWakeLock
{
    public bool IsHeld { get; private set; }

    public async Task<bool> AcquireAsync()
    {
        IsHeld = await (await browser.GetAsync()).InvokeAsync<bool>("wakeLockAcquire");
        return IsHeld;
    }

    public async Task ReleaseAsync()
    {
        await (await browser.GetAsync()).InvokeVoidAsync("wakeLockRelease");
        IsHeld = false;
    }
}
