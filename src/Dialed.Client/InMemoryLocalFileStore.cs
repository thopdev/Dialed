using Dialed.Client.Platform;

namespace Dialed.Client;

/// <summary>A file store that keeps everything in memory: for tests and hosts without persistent storage yet.</summary>
public sealed class InMemoryLocalFileStore : ILocalFileStore
{
    private readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);

    public Task SaveAsync(string path, byte[] content, CancellationToken cancellationToken = default)
    {
        files[path] = content;
        return Task.CompletedTask;
    }

    public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(files.GetValueOrDefault(path));

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(files.ContainsKey(path));

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        files.Remove(path);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListAsync(string folder, CancellationToken cancellationToken = default)
    {
        var prefix = folder.TrimEnd('/') + "/";
        IReadOnlyList<string> result = [.. files.Keys.Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
        return Task.FromResult(result);
    }
}
