namespace Dialed.Client.Sync;

/// <summary>
/// What the engine remembers between syncs: the outbox, the server version of each record, how far each table was pulled
/// and the conflict log. In memory today; with the local database (#39) it lives in SQLite next to the data.
/// </summary>
public interface ISyncState
{
    /// <summary>Raised when a record is added to the outbox (a local change that needs syncing).</summary>
    event Action? OutboxChanged;

    /// <summary>Adds the record to the outbox unless it is already waiting there.</summary>
    Task EnqueueAsync(string table, string entityId, CancellationToken cancellationToken = default);

    /// <summary>Waiting entries, oldest first.</summary>
    Task<IReadOnlyList<OutboxEntry>> PendingAsync(CancellationToken cancellationToken = default);

    Task<bool> HasPendingAsync(string table, string entityId, CancellationToken cancellationToken = default);

    Task RemoveAsync(long sequence, CancellationToken cancellationToken = default);

    Task RecordFailureAsync(long sequence, string error, CancellationToken cancellationToken = default);

    /// <summary>Forgets the failed attempts of an entry so it is tried again as if new.</summary>
    Task ResetFailureAsync(long sequence, CancellationToken cancellationToken = default);

    Task<string?> GetServerVersionAsync(string table, string entityId, CancellationToken cancellationToken = default);

    Task SetServerVersionAsync(string table, string entityId, string version, CancellationToken cancellationToken = default);

    Task<SyncWatermark?> GetWatermarkAsync(string table, CancellationToken cancellationToken = default);

    Task SetWatermarkAsync(string table, SyncWatermark watermark, CancellationToken cancellationToken = default);

    /// <summary>Forgets server versions and pull positions (not the outbox). After the household changes everything is pulled afresh.</summary>
    Task ResetServerStateAsync(CancellationToken cancellationToken = default);

    Task AddConflictAsync(ConflictLogEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConflictLogEntry>> ConflictsAsync(CancellationToken cancellationToken = default);
}
