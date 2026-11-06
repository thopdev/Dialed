using Dialed.Client.Platform;
using Microsoft.JSInterop;

namespace Dialed.Web.Platform;

internal sealed class BrowserAppLifecycle(BrowserModule browser) : IAppLifecycle
{
    public bool IsForeground { get; private set; } = true;

    public event Action? Resumed;

    public event Action? Paused;

    /// <summary>Starts listening for visibility changes. Called once at startup.</summary>
    public async Task InitializeAsync()
    {
        IsForeground = await (await browser.GetAsync()).InvokeAsync<bool>("watchVisibility", DotNetObjectReference.Create(this));
    }

    [JSInvokable]
    public void OnVisibilityChanged(bool visible)
    {
        if (visible == IsForeground)
        {
            return;
        }

        IsForeground = visible;
        (visible ? Resumed : Paused)?.Invoke();
    }
}
