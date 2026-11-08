using System.Text.Json;
using Dialed.Client.Account;

namespace Dialed.Web.Platform;

/// <summary>Keeps the sign-in in localStorage so the app stays signed in between launches. A damaged value counts as signed out.</summary>
internal sealed class BrowserSessionStore(BrowserModule browser) : ISessionStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SessionTokens?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var json = await (await browser.GetAsync()).InvokeAsync<string?>("sessionLoad", cancellationToken);
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SessionTokens>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(SessionTokens tokens, CancellationToken cancellationToken = default) =>
        await (await browser.GetAsync()).InvokeVoidAsync("sessionSave", cancellationToken, JsonSerializer.Serialize(tokens, Json));

    public async Task ClearAsync(CancellationToken cancellationToken = default) =>
        await (await browser.GetAsync()).InvokeVoidAsync("sessionClear", cancellationToken);
}
