using Dialed.Client.Platform;
using Microsoft.JSInterop;

namespace Dialed.Web.Platform;

/// <summary>WebAuthn through navigator.credentials; options and results are the JSON of the API's passkey endpoints (ADR-0010).</summary>
internal sealed class BrowserPasskeyClient(BrowserModule browser) : IPasskeyClient
{
    public async Task<bool> IsSupportedAsync() =>
        await (await browser.GetAsync()).InvokeAsync<bool>("passkeysSupported");

    public async Task<bool> IsAutofillSupportedAsync() =>
        await (await browser.GetAsync()).InvokeAsync<bool>("passkeysAutofillSupported");

    public Task<PasskeyOutcome> CreateAsync(string optionsJson) => RunAsync("passkeyCreate", optionsJson, false);

    public Task<PasskeyOutcome> GetAsync(string optionsJson, bool autofill = false) => RunAsync("passkeyGet", optionsJson, autofill);

    public async Task CancelPendingAsync() =>
        await (await browser.GetAsync()).InvokeVoidAsync("passkeyCancel");

    private async Task<PasskeyOutcome> RunAsync(string function, string optionsJson, bool autofill)
    {
        var result = await (await browser.GetAsync()).InvokeAsync<JsOutcome>(function, optionsJson, autofill);
        var status = result.Status switch
        {
            "success" => PasskeyStatus.Success,
            "cancelled" => PasskeyStatus.Cancelled,
            "unsupported" => PasskeyStatus.NotSupported,
            _ => PasskeyStatus.Failed,
        };
        return new PasskeyOutcome(status, result.ResponseJson, result.Error);
    }

    private sealed record JsOutcome(string Status, string? ResponseJson, string? Error);
}
