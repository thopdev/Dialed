using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dialed.Client.Account;
using Dialed.Client.Platform;
using Dialed.Client.Sync;

namespace Dialed.Client.Push;

/// <summary>What the person switched on, and how many of their devices are registered.</summary>
public sealed record PushSettings(bool Follows, bool Household, int Devices, bool LowStock = false, bool Peak = false, bool Service = false);

/// <summary>The API's push endpoints (see Dialed.Api, #100). Online only.</summary>
public interface IPushApi
{
    /// <summary>The server's public VAPID key; <c>null</c> when this server does not send push.</summary>
    Task<string?> GetKeyAsync(CancellationToken cancellationToken = default);

    Task SubscribeAsync(PushDevice device, string language, CancellationToken cancellationToken = default);

    Task UnsubscribeAsync(string endpoint, CancellationToken cancellationToken = default);

    Task<PushSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the switches. For the stock alerts the server also needs the inventory settings and the time zone to judge bags like the app does.</summary>
    Task<PushSettings> SaveSettingsAsync(bool follows, bool household, bool lowStock, bool peak, Dialed.Domain.Inventory.InventorySettings? inventory, string? timeZone, bool service = false, CancellationToken cancellationToken = default);
}

public sealed class PushApi(HttpClient http, IAccessTokenProvider tokens) : IPushApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record KeyDto(string PublicKey);

    public async Task<string?> GetKeyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return (await SendAsync<KeyDto>(HttpMethod.Get, "push/key", null, cancellationToken)).PublicKey;
        }
        catch (AccountRefusedException e) when (e.Status == 503)
        {
            return null;
        }
    }

    public async Task SubscribeAsync(PushDevice device, string language, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        (await SendRawAsync(HttpMethod.Put, "push/subscription", new { device.Endpoint, device.P256dh, device.Auth, Language = language }, cancellationToken)).Dispose();
    }

    public async Task UnsubscribeAsync(string endpoint, CancellationToken cancellationToken = default) =>
        (await SendRawAsync(HttpMethod.Post, "push/unsubscribe", new { endpoint }, cancellationToken)).Dispose();

    public Task<PushSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<PushSettings>(HttpMethod.Get, "push/settings", null, cancellationToken);

    public Task<PushSettings> SaveSettingsAsync(bool follows, bool household, bool lowStock, bool peak, Dialed.Domain.Inventory.InventorySettings? inventory, string? timeZone, bool service = false, CancellationToken cancellationToken = default) =>
        SendAsync<PushSettings>(HttpMethod.Put, "push/settings", new { follows, household, lowStock, peak, inventory, timeZone, service }, cancellationToken);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendRawAsync(method, path, body, cancellationToken);
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

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
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

        if (response.StatusCode == HttpStatusCode.Unauthorized)
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
