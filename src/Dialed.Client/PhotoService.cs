using System.Collections.Concurrent;
using Dialed.Client.Data;
using Dialed.Client.Platform;
using Dialed.Client.Uploads;
using Dialed.Domain.Model;

namespace Dialed.Client;

/// <summary>
/// Adds, reads and removes photos: the JPEG goes to the local file store, the metadata to the synced <see cref="Photo"/> table, and with
/// an upload queue (#68) the file is then uploaded in the background. The file is written first, so a record never points at a missing file.
/// </summary>
public sealed class PhotoService(IRepository<Photo> photos, ILocalFileStore files, IUploadQueue? uploads = null, IPhotoUploadApi? api = null, TimeProvider? time = null)
{
    private readonly ConcurrentDictionary<string, PhotoUrl> readUrls = new(StringComparer.Ordinal);
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    public async Task<Photo> AddAsync(CapturedPhoto captured, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(captured);

        var photo = new Photo { ContentType = captured.ContentType, SizeBytes = captured.Content.LongLength };
        await files.SaveAsync(photo.LocalPath, captured.Content, cancellationToken);
        var saved = await photos.SaveAsync(photo, cancellationToken);
        if (uploads is not null)
        {
            await uploads.EnqueueAsync(saved.Id, cancellationToken);
        }

        return saved;
    }

    /// <summary>The photo as a <c>data:</c> URI for an img tag, or <c>null</c> when the file is not on this device.</summary>
    public async Task<string?> GetDataUriAsync(string photoId, CancellationToken cancellationToken = default)
    {
        var bytes = await files.ReadAsync(Photo.LocalPathFor(photoId), cancellationToken);
        return bytes is null ? null : $"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}";
    }

    /// <summary>
    /// Something an img tag can show: the file on this device when it is here, otherwise a short-lived URL to the uploaded photo (taken on
    /// another device, or the local copy was cleared). <c>null</c> when neither is available.
    /// </summary>
    public async Task<string?> GetImageSourceAsync(string photoId, CancellationToken cancellationToken = default)
    {
        if (await GetDataUriAsync(photoId, cancellationToken) is { } local)
        {
            return local;
        }

        if (api is null || await photos.GetAsync(photoId, cancellationToken) is not { UploadState: UploadState.Uploaded })
        {
            return null;
        }

        // Reuse a URL while it has a minute left, so a list of photos does not ask the API again on every render.
        if (readUrls.TryGetValue(photoId, out var cached) && cached.ExpiresAt - clock.GetUtcNow() > TimeSpan.FromMinutes(1))
        {
            return cached.Url.ToString();
        }

        var url = await api.GetReadUrlAsync(photoId, cancellationToken);
        if (url is null)
        {
            return null;
        }

        readUrls[photoId] = url;
        return url.Url.ToString();
    }

    /// <summary>Where the photo is in the upload queue on this device, or <c>null</c> when it is not waiting (or there is no queue).</summary>
    public UploadStatus? UploadStatusOf(string photoId) => uploads?.StatusOf(photoId);

    public async Task RemoveAsync(string photoId, CancellationToken cancellationToken = default)
    {
        await photos.DeleteAsync(photoId, cancellationToken);
        if (uploads is not null)
        {
            await uploads.RemoveAsync(photoId, cancellationToken);
        }

        await files.DeleteAsync(Photo.LocalPathFor(photoId), cancellationToken);
        readUrls.TryRemove(photoId, out _);
    }
}
