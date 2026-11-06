using Dialed.Client.Platform;

namespace Dialed.Web.Platform;

internal sealed class BrowserSoundCue(BrowserModule browser) : ISoundCue
{
    public async Task PlayAsync(SoundCue cue) =>
        await (await browser.GetAsync()).InvokeVoidAsync("playCue", cue.ToString().ToLowerInvariant());
}
