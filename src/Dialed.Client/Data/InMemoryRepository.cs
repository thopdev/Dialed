using System.Text.Json;
using Dialed.Client.Storage;
using Dialed.Client.Sync;
using Dialed.Domain.Model;

namespace Dialed.Client.Data;

/// <summary>
/// Records held in memory, with stamping, versions and soft delete. When a <see cref="ILocalStore"/> is given every change is
/// written through to it and <see cref="LoadAsync"/> reads it back at startup, so the data survives a restart.
/// </summary>
public sealed class InMemoryRepository<T>(ICurrentContext context, TimeProvider time, ILocalStore? store = null) : ISyncRepository<T>, ILoadable
    where T : SyncEntity
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string Collection = typeof(T).Name;

    private readonly Dictionary<string, T> records = [];
    private readonly Lock gate = new();

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (store is null)
        {
            return;
        }

        var documents = await store.ReadAllAsync(Collection, cancellationToken);
        lock (gate)
        {
            records.Clear();
            foreach (var json in documents.Values)
            {
                if (JsonSerializer.Deserialize<T>(json, Json) is { } record)
                {
                    records[record.Id] = record;
                }
            }
        }

        Changed?.Invoke();
    }

    private Task PersistAsync(T record, CancellationToken cancellationToken) =>
        store is null ? Task.CompletedTask : store.PutAsync(Collection, record.Id, JsonSerializer.Serialize(record, Json), cancellationToken);

    public event Action? Changed;

    public event Action<T>? LocalChanged;

    public Task<T?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(records.TryGetValue(id, out var record) && !record.Deleted ? record : null);
        }
    }

    public Task<IReadOnlyList<T>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<T>>([.. records.Values.Where(record => !record.Deleted)]);
        }
    }

    public async Task<T> SaveAsync(T entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        T saved;
        lock (gate)
        {
            var now = time.GetUtcNow();
            if (records.TryGetValue(entity.Id, out var existing))
            {
                if (existing.Version != entity.Version)
                {
                    throw new ConflictException(entity.Id, entity.Version, existing.Version);
                }

                // Creation details and identity belong to the stored record, whatever the edited copy says.
                saved = entity with
                {
                    HouseholdId = existing.HouseholdId,
                    CreatedBy = existing.CreatedBy,
                    CreatedAt = existing.CreatedAt,
                    UpdatedBy = context.UserId,
                    UpdatedAt = now,
                    Version = existing.Version + 1,
                };
            }
            else
            {
                saved = entity with
                {
                    HouseholdId = context.HouseholdId,
                    CreatedBy = context.UserId,
                    CreatedAt = now,
                    UpdatedBy = context.UserId,
                    UpdatedAt = now,
                    Version = 1,
                };
            }

            records[saved.Id] = saved;
        }

        await PersistAsync(saved, cancellationToken);
        Changed?.Invoke();
        LocalChanged?.Invoke(saved);
        return saved;
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        T? deleted = null;
        lock (gate)
        {
            if (records.TryGetValue(id, out var existing) && !existing.Deleted)
            {
                deleted = existing with
                {
                    Deleted = true,
                    UpdatedBy = context.UserId,
                    UpdatedAt = time.GetUtcNow(),
                    Version = existing.Version + 1,
                };
                records[id] = deleted;
            }
        }

        if (deleted is not null)
        {
            await PersistAsync(deleted, cancellationToken);
            Changed?.Invoke();
            LocalChanged?.Invoke(deleted);
        }
    }

    public Task<T?> GetIncludingDeletedAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(records.GetValueOrDefault(id));
        }
    }

    public async Task<T> ApplyRemoteAsync(T remote, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);

        T stored;
        lock (gate)
        {
            // Open edit forms hold the old version, so a remote change bumps it and they see a conflict instead of overwriting.
            stored = remote with { Version = (records.TryGetValue(remote.Id, out var existing) ? existing.Version : 0) + 1 };
            records[stored.Id] = stored;
        }

        await PersistAsync(stored, cancellationToken);
        Changed?.Invoke();
        return stored;
    }

    public async Task<int> PurgeForeignAsync(CancellationToken cancellationToken = default)
    {
        List<string> removed;
        lock (gate)
        {
            removed = [.. records.Values
                .Where(r => r.HouseholdId != context.HouseholdId && r.HouseholdId != LocalContext.LocalHouseholdId && r.CreatedBy != LocalContext.LocalUserId)
                .Select(r => r.Id)];
            foreach (var id in removed)
            {
                records.Remove(id);
            }
        }

        if (removed.Count > 0)
        {
            if (store is not null)
            {
                foreach (var id in removed)
                {
                    await store.RemoveAsync(Collection, id, cancellationToken);
                }
            }

            Changed?.Invoke();
        }

        return removed.Count;
    }

    public async Task<int> AdoptAsync(CancellationToken cancellationToken = default)
    {
        List<T> adopted = [];
        lock (gate)
        {
            var now = time.GetUtcNow();
            foreach (var record in records.Values.Where(r => r.HouseholdId == LocalContext.LocalHouseholdId && r.HouseholdId != context.HouseholdId).ToList())
            {
                var stamped = record with
                {
                    HouseholdId = context.HouseholdId,
                    CreatedBy = context.UserId,
                    UpdatedBy = context.UserId,
                    UpdatedAt = now,
                    Version = record.Version + 1,
                };
                records[stamped.Id] = stamped;
                adopted.Add(stamped);
            }
        }

        if (adopted.Count > 0)
        {
            foreach (var record in adopted)
            {
                await PersistAsync(record, cancellationToken);
            }

            Changed?.Invoke();
            foreach (var record in adopted)
            {
                LocalChanged?.Invoke(record);
            }
        }

        return adopted.Count;
    }
}
