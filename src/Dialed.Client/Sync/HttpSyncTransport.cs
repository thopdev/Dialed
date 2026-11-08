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
        using var request = new HttpRequestMessage(HttpMethod.Put, $"sync/{Uri.EscapeDataString(table)}/{Uri.EscapeDataString(entity.Id)}")
        {
            Content = JsonContent.Create(entity, options: Json),
        };
        if (ifMatchVersion is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatchVersion}\"");
        }

        using var response = await SendAsync(request, cancellationToken);
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

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new SyncTransportException($"Server answered {(int)response.StatusCode}.");
        }

        var page = await Read<PageDto<T>>(response, cancellationToken);
        return new PullPage<T>([.. page.Records.Select(r => new ServerRecord<T>(r.Entity, r.Version))], page.HasMore);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokens.GetAccessTokenAsync(cancellationToken)
            ?? throw new SyncTransportException("Not signed in.");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                throw new SyncTransportException("Sign-in expired.");
            }

            return response;
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
