using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Dialed.Images;

namespace Dialed.Api.Photos;

/// <summary>Blob Storage settings, under <c>Storage</c> in configuration (set by <c>infra/main.bicep</c>).</summary>
public sealed class BlobStorageOptions
{
    public const string Section = "Storage";

    /// <summary>The account's blob endpoint, such as <c>https://stdialeddev.blob.core.windows.net/</c>. Set: photos live in Blob Storage.</summary>
    public string? BlobEndpoint { get; set; }

    /// <summary>The private container for originals (<c>photos/...</c>) and thumbnails (<c>thumbnails/...</c>).</summary>
    public string PhotosContainer { get; set; } = "photos";

    /// <summary>How often <see cref="BlobStoragePhotoWatcher"/> looks for new uploads.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>One blob as the watcher sees it in a listing.</summary>
public sealed record PhotoBlobInfo(string Path, string ETag, long Size, DateTimeOffset LastModified);

/// <summary>Lists blobs under a prefix. <see cref="AzureBlobPhotoStore"/> in Azure; a fake in tests.</summary>
public interface IPhotoBlobListing
{
    IAsyncEnumerable<PhotoBlobInfo> ListAsync(string prefix, CancellationToken cancellationToken = default);
}

/// <summary>
/// Photos in a private Blob Storage container, with the store path as the blob name (<c>photos/{household}/{photo}.jpg</c>,
/// <c>thumbnails/{household}/{photo}-{size}.jpg</c>). The API reaches it with its managed identity; the app never does, it gets
/// user delegation SAS URLs from <see cref="AzureBlobPhotoUrls"/>. Writes raise no event: uploads go straight from the app to the
/// container, so <see cref="BlobStoragePhotoWatcher"/> finds new originals by listing, as the blob trigger of a Function would.
/// </summary>
public sealed class AzureBlobPhotoStore(BlobContainerClient container) : IPhotoBlobStore, IPhotoBlobListing
{
    public async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var download = await Blob(path).DownloadContentAsync(cancellationToken);
            return download.Value.Content.ToArray();
        }
        catch (RequestFailedException error) when (error.Status == StatusCodes.Status404NotFound)
        {
            return null;
        }
    }

    public async Task WriteAsync(string path, byte[] content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        await Blob(path).UploadAsync(
            BinaryData.FromBytes(content),
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "image/jpeg" } },
            cancellationToken);
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        (await Blob(path).ExistsAsync(cancellationToken)).Value;

    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default) =>
        await Blob(path).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);

    public async IAsyncEnumerable<PhotoBlobInfo> ListAsync(
        string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in container.GetBlobsAsync(new GetBlobsOptions { Prefix = prefix }, cancellationToken))
        {
            yield return new PhotoBlobInfo(
                item.Name,
                item.Properties.ETag?.ToString() ?? "",
                item.Properties.ContentLength ?? 0,
                item.Properties.LastModified ?? DateTimeOffset.MinValue);
        }
    }

    private BlobClient Blob(string path) =>
        PhotoStoragePaths.IsValid(path) ? container.GetBlobClient(path) : throw new ArgumentException("Not a valid photo path.", nameof(path));
}
