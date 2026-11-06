using Dialed.Client;
using Microsoft.JSInterop;

namespace Dialed.Web;

/// <summary>Browser implementation of <see cref="IPwaState"/>, backed by wwwroot/js/pwa.js and the service worker.</summary>
internal sealed class PwaState(IJSRuntime js) : IPwaState
{
    private IJSObjectReference? module;

    public bool UpdateAvailable { get; private set; }

    public bool IsInstalled { get; private set; }

    public bool NeedsInstallGuide { get; private set; }

    public event Action? Changed;

    /// <summary>Registers the service worker and reads the install state. Called once at startup.</summary>
    public async Task InitializeAsync()
    {
        module = await js.InvokeAsync<IJSObjectReference>("import", "./js/pwa.js");
        var info = await module.InvokeAsync<PwaInfo>("initialize", DotNetObjectReference.Create(this));
        IsInstalled = info.IsStandalone;
        NeedsInstallGuide = info.IsIosSafari && !info.IsStandalone;
        Changed?.Invoke();
    }

    [JSInvokable]
    public void OnUpdateAvailable()
    {
        UpdateAvailable = true;
        Changed?.Invoke();
    }

    public async Task ApplyUpdateAsync()
    {
        if (module is not null)
        {
            await module.InvokeVoidAsync("applyUpdate");
        }
    }

    private sealed record PwaInfo(bool IsStandalone, bool IsIosSafari);
}
