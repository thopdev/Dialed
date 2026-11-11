using Dialed.Images;

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
/// Runs after a photo arrived in storage. Azure: an Event Grid "blob created" event triggers a Function (metadata stripping and
/// thumbnails, #69). The local stand-in calls every registered handler right after the upload, so the same work can run in development.
/// </summary>
public interface IPhotoUploadedHandler
{
    Task OnUploadedAsync(PhotoBlobRef blob, CancellationToken cancellationToken = default);
}

/// <summary>
/// Photos as files in a local folder, laid out like the blob container (<c>photos/{household}/{photo}.jpg</c>, <c>thumbnails/...</c>). The
/// development stand-in for Blob Storage when <see cref="PhotoStorageOptions.LocalFolder"/> is set: the same store the processing uses, so
/// an uploaded photo is stripped and thumbnailed in place and the read URL serves the processed file.
/// </summary>
public sealed class FileSystemPhotoBlobStore(string root) : IPhotoBlobStore, IPhotoBlobEvents
{
    public event Action<string>? BlobWritten;

    public async Task WriteAsync(string path, byte[] content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var full = FullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);

        // Write next to the target and move, so a reader never sees half a photo.
        var temporary = full + ".part";
        await File.WriteAllBytesAsync(temporary, content, cancellationToken);
        File.Move(temporary, full, overwrite: true);
        BlobWritten?.Invoke(path);
    }

    public async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var full = FullPath(path);
        return File.Exists(full) ? await File.ReadAllBytesAsync(full, cancellationToken) : null;
    }

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(File.Exists(FullPath(path)));

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        File.Delete(FullPath(path));
        return Task.CompletedTask;
    }

    /// <summary>Every segment of a blob path is letters, digits, dashes, underscores or a dot inside the file name, and never <c>..</c>, so a path can never leave the folder.</summary>
    private string FullPath(string path)
    {
        var segments = path.Split('/');
        if (segments.Length < 2 || segments.Any(s => s.Length == 0 || s is "." or ".." || !s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
        {
            throw new ArgumentException("Not a valid photo path.", nameof(path));
        }

        return System.IO.Path.Combine([root, .. segments]);
    }
}
