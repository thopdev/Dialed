using System.Collections.Concurrent;

namespace Dialed.Images;

/// <summary>
/// Photo files by blob path (<see cref="PhotoBlobPaths"/>). In memory for now; Azure Blob Storage (private container) replaces it, and
/// its blob-created event takes over from <see cref="InMemoryPhotoBlobStore.BlobWritten"/>.
/// </summary>
public interface IPhotoBlobStore
{
    Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default);

    Task WriteAsync(string path, byte[] content, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);

    Task DeleteAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class InMemoryPhotoBlobStore : IPhotoBlobStore
{
    private readonly ConcurrentDictionary<string, byte[]> blobs = new(StringComparer.Ordinal);

    /// <summary>Raised after every write, like Blob Storage's blob-created event. The in-process trigger listens to it.</summary>
    public event Action<string>? BlobWritten;

    public IReadOnlyCollection<string> Paths => [.. blobs.Keys];

    public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(blobs.TryGetValue(path, out var content) ? content.ToArray() : null);

    public Task WriteAsync(string path, byte[] content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        blobs[path] = content.ToArray();
        BlobWritten?.Invoke(path);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(blobs.ContainsKey(path));

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        blobs.TryRemove(path, out _);
        return Task.CompletedTask;
    }
}
