using Dialed.Domain.Model;

namespace Dialed.Client.Data;

/// <summary>An edit started from an older copy of the record than the one in the store.</summary>
public sealed class ConflictException(string id, int expectedVersion, int actualVersion)
    : Exception($"Record {id} is at version {actualVersion}, the edit started from version {expectedVersion}.")
{
    public string Id { get; } = id;
}

/// <summary>
/// Stores one kind of record. Screens only talk to this; the implementation is in memory today and becomes the local
/// SQLite database with sync (#39) without the screens changing.
/// </summary>
public interface IRepository<T>
    where T : SyncEntity
{
    /// <summary>The record, or <c>null</c> when it does not exist or was deleted.</summary>
    Task<T?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>All records that are not deleted, in no particular order.</summary>
    Task<IReadOnlyList<T>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the record or saves a change. The store fills in household, user, timestamps and the new version and returns
    /// the saved record. Throws <see cref="ConflictException"/> when the record changed since it was read.
    /// </summary>
    Task<T> SaveAsync(T entity, CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes the record (no error when it does not exist).</summary>
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Brings a soft-deleted record back (undo). Stored as a normal new version, so it syncs like any edit. No error when it is not deleted.</summary>
    Task RestoreAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Raised after every save or delete so lists can refresh.</summary>
    event Action? Changed;
}
