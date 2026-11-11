using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dialed.Client.Sync;

namespace Dialed.Client.Uploads;

/// <summary>A short-lived URL for one photo, as the API hands it out: where to send it, how, and with which extra headers.</summary>
public sealed record PhotoUrl(Uri Url, string Method, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt);

/// <summary>Why an upload did not work, which decides what the queue does next.</summary>
public enum UploadProblem
{
    /// <summary>No network or the server could not be reached: wait and try again, this does not count as a failure.</summary>
    Offline,

    /// <summary>Not signed in (or the sign-in expired): wait until there is one.</summary>
    SignedOut,

    /// <summary>The server does not know the photo yet (its details have not synced): try again after the next sync.</summary>
    NotSynced,

    /// <summary>The server or storage had a problem: try again later, counts towards giving up.</summary>
    Transient,

    /// <summary>The photo was refused (wrong type, too large, no access): trying again will not help.</summary>
    Rejected,
}

public sealed class PhotoUploadException(UploadProblem problem, string? detail = null)
    : Exception(detail ?? problem.ToString())
{
    public UploadProblem Problem { get; } = problem;
}

/// <summary>
/// The API side of photo files (#68): short-lived URLs to upload and read a photo, and the upload itself. Throws
/// <see cref="PhotoUploadException"/>.
/// </summary>
public interface IPhotoUploadApi
{
    /// <summary>A URL that allows uploading this photo once, for a few minutes.</summary>
    Task<PhotoUrl> GetUploadUrlAsync(string photoId, CancellationToken cancellationToken = default);

    /// <summary>Sends the JPEG to the URL from <see cref="GetUploadUrlAsync"/>.</summary>
    Task UploadAsync(PhotoUrl target, byte[] content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>A URL to show the photo on a device that does not have the file, or <c>null</c> when there is none (yet).</summary>
    Task<PhotoUrl?> GetReadUrlAsync(string photoId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Calls <c>POST /photos/{id}/upload-url</c> and <c>GET /photos/{id}/url</c> on the API with the person's token, and sends the file to the
/// URL it gets back without one: the URL carries its own permission (an Azure SAS, or the API's signed stand-in).
/// </summary>
public sealed class PhotoUploadApi(HttpClient api, HttpClient storage, IAccessTokenProvider tokens) : IPhotoUploadApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Response(string Url, string Method, Dictionary<string, string>? Headers, DateTimeOffset ExpiresAt);

    public async Task<PhotoUrl> GetUploadUrlAsync(string photoId, CancellationToken cancellationToken = default) =>
        await RequestUrlAsync(HttpMethod.Post, $"photos/{Uri.EscapeDataString(photoId)}/upload-url", cancellationToken)
        ?? throw new PhotoUploadException(UploadProblem.NotSynced);

    public async Task<PhotoUrl?> GetReadUrlAsync(string photoId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await RequestUrlAsync(HttpMethod.Get, $"photos/{Uri.EscapeDataString(photoId)}/url", cancellationToken);
        }
        catch (PhotoUploadException)
        {
            return null;
        }
    }

    public async Task UploadAsync(PhotoUrl target, byte[] content, string contentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(content);

        using var request = new HttpRequestMessage(new HttpMethod(target.Method), target.Url) { Content = new ByteArrayContent(content) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        foreach (var (name, value) in target.Headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await SendAsync(storage, request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw response.StatusCode switch
            {
                // An expired or refused URL: a fresh one on the next attempt may work.
                HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => new PhotoUploadException(UploadProblem.Transient, "The upload URL was refused."),
                HttpStatusCode.UnsupportedMediaType or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest
                    => new PhotoUploadException(UploadProblem.Rejected, $"Storage refused the photo ({(int)response.StatusCode})."),
                _ => new PhotoUploadException(UploadProblem.Transient, $"Storage answered {(int)response.StatusCode}."),
            };
        }
    }

    private async Task<PhotoUrl?> RequestUrlAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        string? token;
        try
        {
            token = await tokens.GetAccessTokenAsync(cancellationToken);
        }
        catch (SyncTransportException)
        {
            throw new PhotoUploadException(UploadProblem.Offline);
        }

        if (token is null)
        {
            throw new PhotoUploadException(UploadProblem.SignedOut);
        }

        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await SendAsync(api, request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new PhotoUploadException(UploadProblem.SignedOut),
                _ => new PhotoUploadException(UploadProblem.Transient, $"The API answered {(int)response.StatusCode}."),
            };
        }

        Response? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<Response>(Json, cancellationToken);
        }
        catch (JsonException)
        {
            body = null;
        }

        return body is not null && Uri.TryCreate(body.Url, UriKind.Absolute, out var url)
            ? new PhotoUrl(url, body.Method, body.Headers ?? [], body.ExpiresAt)
            : throw new PhotoUploadException(UploadProblem.Transient, "The API sent no usable URL.");
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new PhotoUploadException(UploadProblem.Offline, exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PhotoUploadException(UploadProblem.Offline, "The request timed out.");
        }
    }
}
