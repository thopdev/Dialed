using Dialed.Domain.Model;

namespace Dialed.Images;

/// <summary>Updates the synced <see cref="Photo"/> record once its thumbnails exist. API: the sync store; later the Function writes to Cosmos DB.</summary>
public interface IPhotoRecords
{
    /// <summary>Sets <see cref="Photo.ThumbPath"/>; does nothing when it already has that value.</summary>
    /// <returns><c>false</c> when the record is not on the server (yet).</returns>
    Task<bool> SetThumbPathAsync(string householdId, string photoId, string thumbPath, CancellationToken cancellationToken = default);
}

/// <summary>The photo's record has not been synced yet. Worth retrying: the app pushes it soon.</summary>
public sealed class PhotoRecordMissingException(string householdId, string photoId)
    : Exception($"Photo {photoId} in household {householdId} has no record on the server yet.");

public enum PhotoProcessingOutcome
{
    /// <summary>Thumbnails written (and the original stripped when it had metadata).</summary>
    Processed,

    /// <summary>Nothing to do: the original was already clean and the thumbnails exist.</summary>
    AlreadyProcessed,

    /// <summary>The blob is gone (deleted after the event was raised).</summary>
    Missing,

    /// <summary>The path is not an original photo (a thumbnail, or something unexpected).</summary>
    Ignored,
}

/// <summary>
/// What runs when an original photo lands in storage (#69, ADR-0008): strip remaining metadata, write the 256 and 768 px thumbnails,
/// set <see cref="Photo.ThumbPath"/>. Idempotent: running it again for the same blob changes nothing. Hosts: the API's in-process
/// trigger today, a blob-triggered Azure Function later; both retry on exceptions and park poison events.
/// </summary>
public sealed class PhotoProcessingHandler(IPhotoBlobStore blobs, IPhotoImageProcessor processor, IPhotoRecords records)
{
    /// <exception cref="InvalidPhotoException">The blob is not a usable photo (do not retry).</exception>
    /// <exception cref="PhotoRecordMissingException">The record is not on the server yet (retry later).</exception>
    public async Task<PhotoProcessingOutcome> HandleAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        if (!PhotoBlobPaths.TryParseOriginal(blobPath, out var householdId, out var photoId))
        {
            return PhotoProcessingOutcome.Ignored;
        }

        var content = await blobs.ReadAsync(blobPath, cancellationToken);
        if (content is null)
        {
            return PhotoProcessingOutcome.Missing;
        }

        var thumbBase = PhotoBlobPaths.ThumbBase(householdId, photoId);
        if (!JpegMetadata.HasMetadata(content) && await AllThumbnailsExistAsync(householdId, photoId, cancellationToken))
        {
            await SetThumbPathAsync(householdId, photoId, thumbBase, cancellationToken);
            return PhotoProcessingOutcome.AlreadyProcessed;
        }

        var processed = processor.Process(content, Photo.ThumbnailSizes);
        foreach (var (size, thumbnail) in processed.Thumbnails)
        {
            await blobs.WriteAsync(PhotoBlobPaths.Thumbnail(householdId, photoId, size), thumbnail, cancellationToken);
        }

        // After the thumbnails: writing the original raises another blob-created event, and that run finds a clean file and
        // thumbnails and stops. Before the record update, so a missing record never leaves the metadata in place.
        if (processed.OriginalChanged)
        {
            await blobs.WriteAsync(blobPath, processed.Original, cancellationToken);
        }

        await SetThumbPathAsync(householdId, photoId, thumbBase, cancellationToken);
        return PhotoProcessingOutcome.Processed;
    }

    private async Task<bool> AllThumbnailsExistAsync(string householdId, string photoId, CancellationToken cancellationToken)
    {
        foreach (var size in Photo.ThumbnailSizes)
        {
            if (!await blobs.ExistsAsync(PhotoBlobPaths.Thumbnail(householdId, photoId, size), cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    private async Task SetThumbPathAsync(string householdId, string photoId, string thumbBase, CancellationToken cancellationToken)
    {
        if (!await records.SetThumbPathAsync(householdId, photoId, thumbBase, cancellationToken))
        {
            throw new PhotoRecordMissingException(householdId, photoId);
        }
    }
}
