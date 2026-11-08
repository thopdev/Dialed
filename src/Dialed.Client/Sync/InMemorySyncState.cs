namespace Dialed.Client.Sync;

public sealed class InMemorySyncState : ISyncState
{
    private readonly Lock gate = new();
    private readonly List<OutboxEntry> outbox = [];
    private readonly Dictionary<(string Table, string Id), string> versions = [];
    private readonly Dictionary<string, SyncWatermark> watermarks = [];
    private readonly List<ConflictLogEntry> conflicts = [];
    private long nextSequence = 1;

    public Task EnqueueAsync(string table, string entityId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (!outbox.Any(e => e.Table == table && e.EntityId == entityId))
            {
                outbox.Add(new OutboxEntry(nextSequence++, table, entityId));
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxEntry>> PendingAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<OutboxEntry>>([.. outbox.OrderBy(e => e.Sequence)]);
        }
    }

    public Task<bool> HasPendingAsync(string table, string entityId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(outbox.Any(e => e.Table == table && e.EntityId == entityId));
        }
    }

    public Task RemoveAsync(long sequence, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            outbox.RemoveAll(e => e.Sequence == sequence);
        }

        return Task.CompletedTask;
    }

    public Task RecordFailureAsync(long sequence, string error, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var index = outbox.FindIndex(e => e.Sequence == sequence);
            if (index >= 0)
            {
                outbox[index] = outbox[index] with { Attempts = outbox[index].Attempts + 1, LastError = error };
            }
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetServerVersionAsync(string table, string entityId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(versions.GetValueOrDefault((table, entityId)));
        }
    }

    public Task SetServerVersionAsync(string table, string entityId, string version, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            versions[(table, entityId)] = version;
        }

        return Task.CompletedTask;
    }

    public Task<SyncWatermark?> GetWatermarkAsync(string table, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(watermarks.GetValueOrDefault(table));
        }
    }

    public Task SetWatermarkAsync(string table, SyncWatermark watermark, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            watermarks[table] = watermark;
        }

        return Task.CompletedTask;
    }

    public Task AddConflictAsync(ConflictLogEntry entry, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            conflicts.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConflictLogEntry>> ConflictsAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<ConflictLogEntry>>([.. conflicts]);
        }
    }
}
