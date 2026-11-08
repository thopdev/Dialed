using Dialed.Client.Sync;
using Dialed.Domain.Model;

namespace Dialed.Client.Tests;

/// <summary>An in-process stand-in for the Datasync server: versions, If-Match, soft delete and paged delta pulls.</summary>
internal sealed class FakeSyncServer(TimeProvider time) : ISyncTransport
{
    private readonly Dictionary<(string Table, string Id), (SyncEntity Entity, int Version)> rows = [];

    public bool Offline { get; set; }

    public int PushCalls { get; private set; }

    public int PullCalls { get; private set; }

    /// <summary>Refuse pushes of these ids for good (like an access check failing).</summary>
    public HashSet<string> RejectIds { get; } = [];

    public IEnumerable<SyncEntity> All(string table) => rows.Where(r => r.Key.Table == table).Select(r => r.Value.Entity);

    public int VersionOf(string table, string id) => rows[(table, id)].Version;

    /// <summary>A change made by another device: stored and versioned like any push.</summary>
    public void Seed<T>(string table, T entity)
        where T : SyncEntity
    {
        var key = (table, entity.Id);
        var version = rows.TryGetValue(key, out var existing) ? existing.Version + 1 : 1;
        rows[key] = (entity with { UpdatedAt = time.GetUtcNow() }, version);
    }

    public Task<PushResult<T>> PushAsync<T>(string table, T entity, string? ifMatchVersion, CancellationToken cancellationToken = default)
        where T : SyncEntity
    {
        if (Offline)
        {
            throw new SyncTransportException("offline");
        }

        PushCalls++;
        if (RejectIds.Contains(entity.Id))
        {
            return Task.FromResult(new PushResult<T>(PushOutcome.Rejected, Message: "no access"));
        }

        var key = (table, entity.Id);
        if (rows.TryGetValue(key, out var existing))
        {
            if (ifMatchVersion != existing.Version.ToString())
            {
                return Task.FromResult(new PushResult<T>(PushOutcome.Conflict, new ServerRecord<T>((T)existing.Entity, existing.Version.ToString())));
            }
        }
        else if (ifMatchVersion is not null)
        {
            return Task.FromResult(new PushResult<T>(PushOutcome.Rejected, Message: "unknown version"));
        }

        var version = (existing.Entity is null ? 0 : existing.Version) + 1;
        var stored = entity with { UpdatedAt = time.GetUtcNow() };
        rows[key] = (stored, version);
        return Task.FromResult(new PushResult<T>(PushOutcome.Accepted, new ServerRecord<T>(stored, version.ToString())));
    }

    public Task<PullPage<T>> PullAsync<T>(string table, SyncWatermark? after, int pageSize, CancellationToken cancellationToken = default)
        where T : SyncEntity
    {
        if (Offline)
        {
            throw new SyncTransportException("offline");
        }

        PullCalls++;
        var matching = rows
            .Where(r => r.Key.Table == table)
            .Select(r => (Entity: (T)r.Value.Entity, r.Value.Version))
            .Where(r => after is null
                || r.Entity.UpdatedAt > after.UpdatedAt
                || (r.Entity.UpdatedAt == after.UpdatedAt && string.CompareOrdinal(r.Entity.Id, after.Id) > 0))
            .OrderBy(r => r.Entity.UpdatedAt)
            .ThenBy(r => r.Entity.Id, StringComparer.Ordinal)
            .ToList();
        var page = matching.Take(pageSize).Select(r => new ServerRecord<T>(r.Entity, r.Version.ToString())).ToList();
        return Task.FromResult(new PullPage<T>(page, matching.Count > pageSize));
    }
}
