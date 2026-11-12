using Dialed.Client.Platform;
using Microsoft.JSInterop;

namespace Dialed.Web.Platform;

internal sealed class BrowserSpeechCue(BrowserModule browser) : ISpeechCue
{
    public async Task<bool> IsSupportedAsync() =>
        await (await browser.GetAsync()).InvokeAsync<bool>("speechSupported");

    public async Task SpeakAsync(string text, string language) =>
        await (await browser.GetAsync()).InvokeVoidAsync("speak", text, language);
}
