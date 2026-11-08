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

    /// <summary>Where the file lives in the local file store.</summary>
    public string LocalPath => LocalPathFor(Id);

    public static string LocalPathFor(string id) => $"photos/{id}.jpg";
}
