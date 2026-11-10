using System.Collections.Concurrent;

namespace Dialed.Api.Photos;

/// <summary>Photo storage settings, under <c>Photos</c> in configuration.</summary>
public sealed class PhotoStorageOptions
{
    public const string Section = "Photos";

    /// <summary>How long an upload URL may be used. Short: the app asks for a new one for every attempt.</summary>
    public TimeSpan UploadUrlLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a read URL may be used. The browser keeps the image after that.</summary>
    public TimeSpan ReadUrlLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Largest accepted photo. The app re-encodes photos to at most 2048 px JPEG, which stays well below this.</summary>
    public long MaxBytes { get; set; } = 5_000_000;

    /// <summary>
    /// Folder for the local stand-in of Blob Storage. Empty keeps photos in memory (tests); Development uses a folder in the temp
    /// directory so photos survive an API restart.
    /// </summary>
    public string? LocalFolder { get; set; }
}

/// <summary>One photo's file in storage: <c>photos/{householdId}/{photoId}.jpg</c> (ADR-0008).</summary>
public sealed record PhotoBlobRef(string HouseholdId, string PhotoId)
{
    public string Path => $"photos/{HouseholdId}/{PhotoId}.jpg";

    /// <summary>Household and photo ids are server-made or GUIDs: letters, digits and dashes only, so they can never escape the folder.</summary>
    public static bool IsSafeId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');
}

/// <summary>A short-lived URL for one photo: the method to use, headers the request must carry, and when it stops working.</summary>
public sealed record PhotoUrl(Uri Url, string Method, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt);

/// <summary>
/// Hands out short-lived URLs that allow exactly one thing on exactly one photo: writing it, or reading it. The caller has done the access
/// check. Azure: a user-delegation SAS on the blob (see <c>docs/photo-uploads.md</c>); here: <see cref="LocalSignedPhotoUrls"/>.
/// </summary>
public interface IPhotoUrlIssuer
{
    PhotoUrl IssueUpload(PhotoBlobRef blob);

    PhotoUrl IssueRead(PhotoBlobRef blob);
}

/// <summary>
/// The photo files. Only the local stand-in writes through this (Azure uploads go straight to Blob Storage); readers such as the label
/// scan or thumbnails (#69) use it to fetch a stored photo by id.
/// </summary>
public interface IPhotoBlobStore
{
    Task WriteAsync(PhotoBlobRef blob, byte[] content, CancellationToken cancellationToken = default);

    /// <summary>The photo, or <c>null</c> when it was never uploaded.</summary>
    Task<byte[]?> ReadAsync(PhotoBlobRef blob, CancellationToken cancellationToken = default);

    /// <summary>Removes the photo; removing a missing one is not an error.</summary>
    Task DeleteAsync(PhotoBlobRef blob, CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs after a photo arrived in storage. Azure: an Event Grid "blob created" event triggers a Function (metadata stripping and
/// thumbnails, #69). The local stand-in calls every registered handler right after the upload, so the same work can run in development.
/// </summary>
public interface IPhotoUploadedHandler
{
    Task OnUploadedAsync(PhotoBlobRef blob, CancellationToken cancellationToken = default);
}

public sealed class InMemoryPhotoBlobStore : IPhotoBlobStore
{
    private readonly ConcurrentDictionary<string, byte[]> files = new(StringComparer.Ordinal);

    public Task WriteAsync(PhotoBlobRef blob, byte[] content, CancellationToken cancellationToken = default)
    {
        files[blob.Path] = content;
        return Task.CompletedTask;
    }

    public Task<byte[]?> ReadAsync(PhotoBlobRef blob, CancellationToken cancellationToken = default) =>
        Task.FromResult(files.GetValueOrDefault(blob.Path));

    public Task DeleteAsync(PhotoBlobRef blob, CancellationToken cancellationToken = default)
    {
        files.TryRemove(blob.Path, out _);
        return Task.CompletedTask;
    }
}

/// <summary>Photos as files in a local folder, laid out like the blob container.</summary>
public sealed class FileSystemPhotoBlobStore(string root) : IPhotoBlobStore
{
    public async Task WriteAsync(PhotoBlobRef blob, byte[] content, CancellationToken cancellationToken = default)
    {
        var path = FullPath(blob);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        // Write next to the target and move, so a reader never sees half a photo.
        var temporary = path + ".part";
        await File.WriteAllBytesAsync(temporary, content, cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    public async Task<byte[]?> ReadAsync(PhotoBlobRef blob, CancellationToken cancellationToken = default)
    {
        var path = FullPath(blob);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
    }

    public Task DeleteAsync(PhotoBlobRef blob, CancellationToken cancellationToken = default)
    {
        File.Delete(FullPath(blob));
        return Task.CompletedTask;
    }

    private string FullPath(PhotoBlobRef blob)
    {
        if (!PhotoBlobRef.IsSafeId(blob.HouseholdId) || !PhotoBlobRef.IsSafeId(blob.PhotoId))
        {
            throw new ArgumentException("Not a valid photo reference.", nameof(blob));
        }

        return System.IO.Path.Combine(root, blob.HouseholdId, blob.PhotoId + ".jpg");
    }
}
