using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dialed.Domain.Model;

namespace Dialed.Client.Sync;

/// <summary>Gives the transport the current access token (the sign-in flow implements it). <c>null</c> means not signed in.</summary>
public interface IAccessTokenProvider
{
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a fresh token after the server refused <paramref name="refusedToken"/> (for example because it expired during a long sync).
    /// <c>null</c> when signed out. Providers that cannot renew simply return the current token.
    /// </summary>
    Task<string?> RenewAccessTokenAsync(string? refusedToken, CancellationToken cancellationToken = default) => GetAccessTokenAsync(cancellationToken);
}

/// <summary>The sync protocol over HTTP: <c>GET /sync/{table}</c> to pull and <c>PUT /sync/{table}/{id}</c> with <c>If-Match</c> to push (see Dialed.Api).</summary>
public sealed class HttpSyncTransport(HttpClient http, IAccessTokenProvider tokens) : ISyncTransport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Dto<T>(T Entity, string Version);

    private sealed record PageDto<T>(List<Dto<T>> Records, bool HasMore);

    public async Task<PushResult<T>> PushAsync<T>(string table, T entity, string? ifMatchVersion, CancellationToken cancellationToken = default)
        where T : SyncEntity
    {
        HttpRequestMessage Build()
        {
            var request = new HttpRequestMessage(HttpMethod.Put, $"sync/{Uri.EscapeDataString(table)}/{Uri.EscapeDataString(entity.Id)}")
            {
                Content = JsonContent.Create(entity, options: Json),
            };
            if (ifMatchVersion is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatchVersion}\"");
            }

            return request;
        }

        using var response = await SendAsync(Build, cancellationToken);
        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                return new PushResult<T>(PushOutcome.Accepted, await ReadRecord<T>(response, cancellationToken));
            case HttpStatusCode.Conflict:
                return new PushResult<T>(PushOutcome.Conflict, await ReadRecord<T>(response, cancellationToken));
            case HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound:
                return new PushResult<T>(PushOutcome.Rejected, Message: $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancellationToken)}");
            default:
                throw new SyncTransportException($"Server answered {(int)response.StatusCode}.");
        }
    }

    public async Task<PullPage<T>> PullAsync<T>(string table, SyncWatermark? after, int pageSize, CancellationToken cancellationToken = default)
        where T : SyncEntity
    {
        var url = $"sync/{Uri.EscapeDataString(table)}?pageSize={pageSize}";
        if (after is not null)
        {
            url += $"&afterUpdatedAt={Uri.EscapeDataString(after.UpdatedAt.ToString("O"))}&afterId={Uri.EscapeDataString(after.Id)}";
        }

        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new SyncTransportException($"Server answered {(int)response.StatusCode}.");
        }

        var page = await Read<PageDto<T>>(response, cancellationToken);
        return new PullPage<T>([.. page.Records.Select(r => new ServerRecord<T>(r.Entity, r.Version))], page.HasMore);
    }

    /// <summary>Sends the request; when the server refuses the token (expired mid-sync) it renews the token once and sends again.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken cancellationToken)
    {
        var token = await tokens.GetAccessTokenAsync(cancellationToken)
            ?? throw new SyncTransportException("Not signed in.");

        var response = await SendOnceAsync(build, token, cancellationToken);
        if (response.StatusCode is not HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        var renewed = await tokens.RenewAccessTokenAsync(token, cancellationToken)
            ?? throw new SyncTransportException("Not signed in.");
        response = await SendOnceAsync(build, renewed, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            throw new SyncTransportException("Sign-in expired.");
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(Func<HttpRequestMessage> build, string token, CancellationToken cancellationToken)
    {
        using var request = build();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            return await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new SyncTransportException("The server could not be reached.", e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SyncTransportException("The server did not answer in time.", e);
        }
    }

    private static async Task<ServerRecord<T>> ReadRecord<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        where T : SyncEntity
    {
        var dto = await Read<Dto<T>>(response, cancellationToken);
        return new ServerRecord<T>(dto.Entity, dto.Version);
    }

    private static async Task<TValue> Read<TValue>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<TValue>(Json, cancellationToken)
                ?? throw new SyncTransportException("The server sent an empty answer.");
        }
        catch (JsonException e)
        {
            throw new SyncTransportException("The server sent an answer the app cannot read.", e);
        }
    }
}
