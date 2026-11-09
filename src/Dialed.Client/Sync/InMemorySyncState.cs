using System.Globalization;
using System.Text.Json;
using Dialed.Client.Storage;

namespace Dialed.Client.Sync;

/// <summary>
/// The sync bookkeeping held in memory. With an <see cref="ILocalStore"/> every change is written through and
/// <see cref="LoadAsync"/> restores it at startup, so waiting changes are still pushed after the app was closed.
/// </summary>
public sealed class InMemorySyncState(ILocalStore? store = null) : ISyncState, ILoadable
{
    private const string OutboxCollection = "_sync_outbox";
    private const string VersionsCollection = "_sync_versions";
    private const string WatermarksCollection = "_sync_watermarks";
    private const string ConflictsCollection = "_sync_conflicts";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lock gate = new();
    private readonly List<OutboxEntry> outbox = [];
    private readonly Dictionary<(string Table, string Id), string> versions = [];
    private readonly Dictionary<string, SyncWatermark> watermarks = [];
    private readonly List<ConflictLogEntry> conflicts = [];
    private long nextSequence = 1;

    public event Action? OutboxChanged;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (store is null)
        {
            return;
        }

        var storedOutbox = await store.ReadAllAsync(OutboxCollection, cancellationToken);
        var storedVersions = await store.ReadAllAsync(VersionsCollection, cancellationToken);
        var storedWatermarks = await store.ReadAllAsync(WatermarksCollection, cancellationToken);
        var storedConflicts = await store.ReadAllAsync(ConflictsCollection, cancellationToken);
        lock (gate)
        {
            outbox.Clear();
            versions.Clear();
            watermarks.Clear();
            conflicts.Clear();
            outbox.AddRange(storedOutbox.Values.Select(json => JsonSerializer.Deserialize<OutboxEntry>(json, Json)!).OrderBy(e => e.Sequence));
            nextSequence = outbox.Count == 0 ? 1 : outbox.Max(e => e.Sequence) + 1;
            foreach (var (key, version) in storedVersions)
            {
                var split = key.IndexOf('|', StringComparison.Ordinal);
                versions[(key[..split], key[(split + 1)..])] = version;
            }

            foreach (var (table, json) in storedWatermarks)
            {
                watermarks[table] = JsonSerializer.Deserialize<SyncWatermark>(json, Json)!;
            }

            conflicts.AddRange(storedConflicts.OrderBy(c => long.Parse(c.Key, CultureInfo.InvariantCulture)).Select(c => JsonSerializer.Deserialize<ConflictLogEntry>(c.Value, Json)!));
        }

        OutboxChanged?.Invoke();
    }

    private Task PutAsync(string collection, string key, object value, CancellationToken cancellationToken) =>
        store is null ? Task.CompletedTask : store.PutAsync(collection, key, JsonSerializer.Serialize(value, Json), cancellationToken);

    public async Task EnqueueAsync(string table, string entityId, CancellationToken cancellationToken = default)
    {
        OutboxEntry? added = null;
        lock (gate)
        {
            if (!outbox.Any(e => e.Table == table && e.EntityId == entityId))
            {
                added = new OutboxEntry(nextSequence++, table, entityId);
                outbox.Add(added);
            }
        }

        if (added is not null)
        {
            await PutAsync(OutboxCollection, added.Sequence.ToString(CultureInfo.InvariantCulture), added, cancellationToken);
            OutboxChanged?.Invoke();
        }
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

        return store is null ? Task.CompletedTask : store.RemoveAsync(OutboxCollection, sequence.ToString(CultureInfo.InvariantCulture), cancellationToken);
    }

    public async Task RecordFailureAsync(long sequence, string error, CancellationToken cancellationToken = default)
    {
        OutboxEntry? updated = null;
        lock (gate)
        {
            var index = outbox.FindIndex(e => e.Sequence == sequence);
            if (index >= 0)
            {
                updated = outbox[index] with { Attempts = outbox[index].Attempts + 1, LastError = error };
                outbox[index] = updated;
            }
        }

        if (updated is not null)
        {
            await PutAsync(OutboxCollection, sequence.ToString(CultureInfo.InvariantCulture), updated, cancellationToken);
        }
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

        return store is null ? Task.CompletedTask : store.PutAsync(VersionsCollection, $"{table}|{entityId}", version, cancellationToken);
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

        return PutAsync(WatermarksCollection, table, watermark, cancellationToken);
    }

    public async Task ResetServerStateAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            versions.Clear();
            watermarks.Clear();
        }

        if (store is not null)
        {
            await store.ClearCollectionAsync(VersionsCollection, cancellationToken);
            await store.ClearCollectionAsync(WatermarksCollection, cancellationToken);
        }
    }

    public Task AddConflictAsync(ConflictLogEntry entry, CancellationToken cancellationToken = default)
    {
        int number;
        lock (gate)
        {
            conflicts.Add(entry);
            number = conflicts.Count;
        }

        return PutAsync(ConflictsCollection, number.ToString(CultureInfo.InvariantCulture), entry, cancellationToken);
    }

    public Task<IReadOnlyList<ConflictLogEntry>> ConflictsAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<ConflictLogEntry>>([.. conflicts]);
        }
    }
}
