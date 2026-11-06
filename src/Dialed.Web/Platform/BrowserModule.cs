using Microsoft.JSInterop;

namespace Dialed.Web.Platform;

/// <summary>Loads wwwroot/js/platform.js once; all browser platform services share it.</summary>
internal sealed class BrowserModule(IJSRuntime js)
{
    private Task<IJSObjectReference>? module;

    public async ValueTask<IJSObjectReference> GetAsync() =>
        await (module ??= js.InvokeAsync<IJSObjectReference>("import", "./js/platform.js").AsTask());
}
