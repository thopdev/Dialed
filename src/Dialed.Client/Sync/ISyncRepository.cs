using Dialed.Client.Data;
using Dialed.Domain.Model;

namespace Dialed.Client.Sync;

/// <summary>A repository the sync engine can read changes from and write server changes into.</summary>
public interface ISyncRepository<T> : IRepository<T>
    where T : SyncEntity
{
    /// <summary>Raised after the user saved or deleted a record here, with the stored record (not for server changes).</summary>
    event Action<T>? LocalChanged;

    /// <summary>The record including soft-deleted ones, or <c>null</c> when it was never stored.</summary>
    Task<T?> GetIncludingDeletedAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Stores a record exactly as the server has it. Does not raise <see cref="LocalChanged"/>, so it is not pushed back.</summary>
    Task<T> ApplyRemoteAsync(T remote, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-stamps every record made before sign-in (the local household) for the current household
    /// and raises <see cref="LocalChanged"/> for each, so it is pushed. Returns how many records changed.
    /// </summary>
    Task<int> AdoptAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the records of every household other than the current one (a device holds one household at a time; the others
    /// stay on the server and come back when the person switches to them). Records made before sign-in stay. Returns how many were removed.
    /// </summary>
    Task<int> PurgeForeignAsync(CancellationToken cancellationToken = default);
}
