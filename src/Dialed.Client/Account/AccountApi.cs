using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Dialed.Client.Account;

/// <summary>The API's account endpoints (see Dialed.Api): passkey ceremonies, refresh and sign-out.</summary>
public sealed class AccountApi(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A started ceremony: the WebAuthn options for the platform and the state to send back with the result.</summary>
    public sealed record Ceremony(string OptionsJson, string State);

    private sealed record CeremonyDto(JsonElement Options, string State);

    public Task<Ceremony> SignUpOptionsAsync(string email, string displayName, CancellationToken cancellationToken = default) =>
        StartAsync("auth/signup/options", new { email, displayName }, cancellationToken);

    public Task<Ceremony> SignInOptionsAsync(CancellationToken cancellationToken = default) =>
        StartAsync("auth/signin/options", new { }, cancellationToken);

    public Task<SessionTokens> SignUpAsync(string state, string credentialJson, CancellationToken cancellationToken = default) =>
        CompleteAsync("auth/signup", state, credentialJson, cancellationToken);

    public Task<SessionTokens> SignInAsync(string state, string credentialJson, CancellationToken cancellationToken = default) =>
        CompleteAsync("auth/signin", state, credentialJson, cancellationToken);

    /// <summary>A new pair for the refresh token, or <c>null</c> when the token is no longer valid (the person must sign in again).</summary>
    public async Task<SessionTokens?> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync("auth/refresh", new { refreshToken }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadAsync<SessionTokens>(response, cancellationToken);
    }

    public async Task SignOutAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync("auth/signout", new { refreshToken }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<Ceremony> StartAsync(string path, object body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(path, body, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var dto = await ReadAsync<CeremonyDto>(response, cancellationToken);
        return new Ceremony(dto.Options.GetRawText(), dto.State);
    }

    private async Task<SessionTokens> CompleteAsync(string path, string state, string credentialJson, CancellationToken cancellationToken)
    {
        using var credential = JsonDocument.Parse(credentialJson);
        using var response = await SendAsync(path, new { state, credential = credential.RootElement }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadAsync<SessionTokens>(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(string path, object body, CancellationToken cancellationToken)
    {
        try
        {
            return await http.PostAsJsonAsync(path, body, Json, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new AccountApiException("The server could not be reached.", e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AccountApiException("The server did not answer in time.", e);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int)response.StatusCode;
        if (status is >= 400 and < 500)
        {
            // ProblemDetails from the API carries a reason that is safe to show.
            string? detail = null;
            try
            {
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                if (document.RootElement.TryGetProperty("detail", out var d))
                {
                    detail = d.GetString();
                }
            }
            catch (JsonException)
            {
            }

            throw new AccountRefusedException(status, detail ?? $"The server refused the request ({status}).");
        }

        throw new AccountApiException($"The server answered {status}.");
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
                ?? throw new AccountApiException("The server sent an empty answer.");
        }
        catch (JsonException e)
        {
            throw new AccountApiException("The server sent an answer the app cannot read.", e);
        }
    }
}
