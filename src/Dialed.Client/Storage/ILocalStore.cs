namespace Dialed.Client.Storage;

/// <summary>
/// Durable storage for the app's own data: small JSON documents grouped in collections (one per record type, plus the sync
/// bookkeeping). Repositories and sync state keep their working copy in memory and write every change through to this store,
/// and read it back once at startup. Web: SQLite in the browser (OPFS or IndexedDB); MAUI: native SQLite; tests: in memory.
/// </summary>
public interface ILocalStore
{
    /// <summary>Every document of the collection by key (empty when the collection does not exist).</summary>
    Task<IReadOnlyDictionary<string, string>> ReadAllAsync(string collection, CancellationToken cancellationToken = default);

    /// <summary>Adds the document or replaces the one with the same key.</summary>
    Task PutAsync(string collection, string key, string json, CancellationToken cancellationToken = default);

    /// <summary>Removes the document; removing a missing one is not an error.</summary>
    Task RemoveAsync(string collection, string key, CancellationToken cancellationToken = default);

    /// <summary>Removes every document of the collection.</summary>
    Task ClearCollectionAsync(string collection, CancellationToken cancellationToken = default);

    /// <summary>Removes everything, for example when signing out on a shared device.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
