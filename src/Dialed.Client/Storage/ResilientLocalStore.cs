namespace Dialed.Client.Storage;

/// <summary>
/// Wraps the device's database so a full or blocked disk does not break saving (#114). A failed write is reported to
/// <see cref="StorageStatus"/> (the app shows a warning) and swallowed: the record stays in memory for this session and, when
/// signed in, still goes to the account, so nothing the person typed is thrown away. The next write that works clears the warning.
/// Reads are not wrapped: failing to read at start-up is handled by the host (the app starts empty and keeps working in memory).
/// </summary>
public sealed class ResilientLocalStore(ILocalStore inner, StorageStatus status) : ILocalStore
{
    public Task<IReadOnlyDictionary<string, string>> ReadAllAsync(string collection, CancellationToken cancellationToken = default) =>
        inner.ReadAllAsync(collection, cancellationToken);

    public Task PutAsync(string collection, string key, string json, CancellationToken cancellationToken = default) =>
        GuardAsync(() => inner.PutAsync(collection, key, json, cancellationToken), cancellationToken);

    public Task RemoveAsync(string collection, string key, CancellationToken cancellationToken = default) =>
        GuardAsync(() => inner.RemoveAsync(collection, key, cancellationToken), cancellationToken);

    public Task ClearCollectionAsync(string collection, CancellationToken cancellationToken = default) =>
        GuardAsync(() => inner.ClearCollectionAsync(collection, cancellationToken), cancellationToken);

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        GuardAsync(() => inner.ClearAsync(cancellationToken), cancellationToken);

    private async Task GuardAsync(Func<Task> write, CancellationToken cancellationToken)
    {
        try
        {
            await write();
            status.ReportWriteSucceeded();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            status.ReportWriteFailed();
        }
    }
}
