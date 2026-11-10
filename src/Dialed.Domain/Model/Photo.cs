namespace Dialed.Domain.Model;

public enum UploadState
{
    /// <summary>Only on this device so far.</summary>
    Pending,
    Uploaded,
    Failed,
}

/// <summary>
/// Metadata of one photo; the JPEG itself lives in the local file store (and later in Blob Storage, ADR-0008).
/// Only this record is synced. Beans, brews, vendors and gear point to photos by id.
/// </summary>
public sealed record Photo : SyncEntity
{
    public string ContentType { get; init; } = "image/jpeg";

    public long SizeBytes { get; init; }

    public UploadState UploadState { get; init; } = UploadState.Pending;

    /// <summary>
    /// Where the server put the thumbnails, without size and extension (for example <c>thumbnails/{household}/{id}</c>); <c>null</c>
    /// until the photo was uploaded and processed (#69). Use <see cref="ThumbnailPath"/> for one size.
    /// </summary>
    public string? ThumbPath { get; init; }

    /// <summary>Longest side, in pixels, of the thumbnails the server makes: 256 for lists and cards, 768 for detail screens.</summary>
    public static IReadOnlyList<int> ThumbnailSizes { get; } = [256, 768];

    /// <summary>The thumbnail of one of the <see cref="ThumbnailSizes"/>, or <c>null</c> when there are none yet.</summary>
    public string? ThumbnailPath(int size) => ThumbPath is null ? null : ThumbnailPathFor(ThumbPath, size);

    public static string ThumbnailPathFor(string thumbPath, int size) => $"{thumbPath}-{size}.jpg";

    /// <summary>Where the file lives in the local file store.</summary>
    public string LocalPath => LocalPathFor(Id);

    public static string LocalPathFor(string id) => $"photos/{id}.jpg";
}
