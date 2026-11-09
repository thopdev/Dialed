using Dialed.Client.Platform;
using Microsoft.JSInterop;

namespace Dialed.Web.Platform;

/// <summary>Preferences in localStorage, next to the sign-in (#226). Blocked storage reads as "nothing saved".</summary>
internal sealed class BrowserPreferenceStore(BrowserModule browser) : IPreferenceStore
{
    public async Task<string?> LoadAsync()
    {
        try
        {
            return await (await browser.GetAsync()).InvokeAsync<string?>("preferencesLoad");
        }
        catch (JSException)
        {
            return null;
        }
    }

    public async Task SaveAsync(string json)
    {
        try
        {
            await (await browser.GetAsync()).InvokeVoidAsync("preferencesSave", json);
        }
        catch (JSException)
        {
            // Blocked: the change lasts until the app closes.
        }
    }
}
