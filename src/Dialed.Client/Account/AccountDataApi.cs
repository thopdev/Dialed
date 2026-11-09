using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dialed.Client.Sync;

namespace Dialed.Client.Account;

/// <summary>The API's account data endpoints (see Dialed.Api): export your data and delete the account (after a passkey check).</summary>
public sealed class AccountDataApi(HttpClient http, IAccessTokenProvider tokens)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record CeremonyDto(JsonElement Options, string State);

    public async Task<byte[]> ExportAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "account/export", null, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<AccountApi.Ceremony> DeleteOptionsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "account/delete/options", new { }, cancellationToken);
        var dto = await response.Content.ReadFromJsonAsync<CeremonyDto>(Json, cancellationToken)
            ?? throw new AccountApiException("The server sent an empty answer.");
        return new AccountApi.Ceremony(dto.Options.GetRawText(), dto.State);
    }

    public async Task DeleteAsync(string state, string credentialJson, CancellationToken cancellationToken = default)
    {
        using var credential = JsonDocument.Parse(credentialJson);
        using var response = await SendAsync(HttpMethod.Post, "account/delete", new { state, credential = credential.RootElement }, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        string token;
        try
        {
            token = await tokens.GetAccessTokenAsync(cancellationToken) ?? throw new AccountApiException("Not signed in.");
        }
        catch (SyncTransportException e)
        {
            throw new AccountApiException(e.Message, e);
        }

        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new AccountApiException("The server could not be reached.", e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AccountApiException("The server did not answer in time.", e);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized && path != "account/delete")
        {
            response.Dispose();
            throw new AccountApiException("Sign-in expired.");
        }

        try
        {
            await ApiResponses.EnsureSuccessAsync(response, cancellationToken);
        }
        catch
        {
            response.Dispose();
            throw;
        }

        return response;
    }
}
