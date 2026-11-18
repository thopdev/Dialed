using Dialed.Domain.Model;
using Dialed.Images;

namespace Dialed.Api.Photos;

/// <summary>
/// Finds photos the app uploaded straight to Blob Storage and raises <see cref="IPhotoBlobEvents.BlobWritten"/> for each, so
/// <see cref="InMemoryPhotoTrigger"/> strips metadata and writes thumbnails (#69). It lists the container every
/// <see cref="BlobStorageOptions.PollInterval"/>: an original needs processing when it has no thumbnails yet, or when it was written
/// again well after its thumbnails (a new upload). Every version (ETag) is raised once per process; after a restart the listing finds
/// whatever was missed. Originals larger than <see cref="PhotoStorageOptions.MaxBytes"/> are deleted, since a SAS cannot limit size.
/// Stands in for the blob-triggered Function, the long-term home of this work (<c>docs/photo-uploads.md</c>).
/// </summary>
public sealed partial class BlobStoragePhotoWatcher(
    IPhotoBlobListing listing,
    IPhotoBlobStore store,
    BlobStorageOptions storage,
    PhotoStorageOptions photos,
    TimeProvider time,
    ILogger<BlobStoragePhotoWatcher> logger) : BackgroundService, IPhotoBlobEvents
{
    /// <summary>
    /// Processing writes the thumbnails and then (when it stripped metadata) the original again, seconds later. An original written
    /// more than this after its thumbnails is a new upload.
    /// </summary>
    public static readonly TimeSpan RewriteWindow = TimeSpan.FromMinutes(1);

    private readonly HashSet<string> raised = new(StringComparer.Ordinal);

    public event Action<string>? BlobWritten;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(storage.PollInterval, time);
        do
        {
            try
            {
                await PollAsync(stoppingToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogPollFailed(error);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One look at the container. Returns the originals it raised an event for.</summary>
    public async Task<IReadOnlyList<string>> PollAsync(CancellationToken cancellationToken = default)
    {
        var thumbnails = new Dictionary<string, (int Count, DateTimeOffset Newest)>(StringComparer.Ordinal);
        await foreach (var thumbnail in listing.ListAsync(PhotoBlobPaths.ThumbnailsFolder + "/", cancellationToken))
        {
            if (ThumbBaseOf(thumbnail.Path) is { } thumbBase)
            {
                var (count, newest) = thumbnails.GetValueOrDefault(thumbBase);
                thumbnails[thumbBase] = (count + 1, thumbnail.LastModified > newest ? thumbnail.LastModified : newest);
            }
        }

        var found = new List<string>();
        await foreach (var original in listing.ListAsync(PhotoBlobPaths.OriginalsFolder + "/", cancellationToken))
        {
            if (!PhotoBlobPaths.TryParseOriginal(original.Path, out var householdId, out var photoId)
                || !raised.Add(original.Path + "|" + original.ETag))
            {
                continue;
            }

            if (original.Size > photos.MaxBytes)
            {
                await store.DeleteAsync(original.Path, cancellationToken);
                LogTooLarge(original.Path, original.Size);
                continue;
            }

            var done = thumbnails.TryGetValue(PhotoBlobPaths.ThumbBase(householdId, photoId), out var thumbs)
                && thumbs.Count >= Photo.ThumbnailSizes.Count
                && original.LastModified <= thumbs.Newest + RewriteWindow;
            if (!done)
            {
                found.Add(original.Path);
            }
        }

        foreach (var path in found)
        {
            BlobWritten?.Invoke(path);
        }

        return found;
    }

    /// <summary><c>thumbnails/h/p-256.jpg</c> to <c>thumbnails/h/p</c>.</summary>
    private static string? ThumbBaseOf(string path)
    {
        var dash = path.LastIndexOf('-');
        return dash > 0 && path.EndsWith(".jpg", StringComparison.Ordinal) ? path[..dash] : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not list the photo container; trying again at the next interval")]
    private partial void LogPollFailed(Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Photo {BlobPath} was {Size} bytes, over the limit; deleted")]
    private partial void LogTooLarge(string blobPath, long size);
}
