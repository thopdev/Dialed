using Dialed.Client.Data;
using Dialed.Client.Platform;
using Dialed.Domain.Model;

namespace Dialed.Client;

/// <summary>
/// Adds, reads and removes photos: the JPEG goes to the local file store, the metadata to the synced <see cref="Photo"/> table.
/// The file is written first, so a record never points at a missing file.
/// </summary>
public sealed class PhotoService(IRepository<Photo> photos, ILocalFileStore files)
{
    public async Task<Photo> AddAsync(CapturedPhoto captured, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(captured);

        var photo = new Photo { ContentType = captured.ContentType, SizeBytes = captured.Content.LongLength };
        await files.SaveAsync(photo.LocalPath, captured.Content, cancellationToken);
        return await photos.SaveAsync(photo, cancellationToken);
    }

    /// <summary>The photo as a <c>data:</c> URI for an img tag, or <c>null</c> when the file is not on this device.</summary>
    public async Task<string?> GetDataUriAsync(string photoId, CancellationToken cancellationToken = default)
    {
        var bytes = await files.ReadAsync(Photo.LocalPathFor(photoId), cancellationToken);
        return bytes is null ? null : $"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}";
    }

    public async Task RemoveAsync(string photoId, CancellationToken cancellationToken = default)
    {
        await photos.DeleteAsync(photoId, cancellationToken);
        await files.DeleteAsync(Photo.LocalPathFor(photoId), cancellationToken);
    }
}
