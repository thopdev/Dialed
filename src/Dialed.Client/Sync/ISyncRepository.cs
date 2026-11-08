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
}
