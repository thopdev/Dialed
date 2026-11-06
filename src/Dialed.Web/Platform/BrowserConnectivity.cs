using Dialed.Client.Platform;
using Microsoft.JSInterop;

namespace Dialed.Web.Platform;

internal sealed class BrowserConnectivity(BrowserModule browser) : IConnectivity
{
    public bool IsOnline { get; private set; } = true;

    public event Action<bool>? Changed;

    /// <summary>Reads the current state and starts listening for changes. Called once at startup.</summary>
    public async Task InitializeAsync()
    {
        IsOnline = await (await browser.GetAsync()).InvokeAsync<bool>("watchConnectivity", DotNetObjectReference.Create(this));
    }

    [JSInvokable]
    public void OnChanged(bool online)
    {
        if (online == IsOnline)
        {
            return;
        }

        IsOnline = online;
        Changed?.Invoke(online);
    }
}
