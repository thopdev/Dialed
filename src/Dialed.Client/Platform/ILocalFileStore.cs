namespace Dialed.Client.Platform;

/// <summary>
/// Private file storage for the app (photos waiting to upload, cached images). Paths use "/" and are
/// relative to the app's own area, for example "photos/abc.jpg". Web: origin private file system; MAUI: app data folder.
/// </summary>
public interface ILocalFileStore
{
    Task SaveAsync(string path, byte[] content, CancellationToken cancellationToken = default);

    /// <summary>Returns the file, or <c>null</c> when it does not exist.</summary>
    Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Deletes the file; deleting a missing file is not an error.</summary>
    Task DeleteAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Paths of all files under <paramref name="folder"/> (recursive), for example "photos".</summary>
    Task<IReadOnlyList<string>> ListAsync(string folder, CancellationToken cancellationToken = default);
}
