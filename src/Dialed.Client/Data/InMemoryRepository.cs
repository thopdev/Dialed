using Dialed.Domain.Model;

namespace Dialed.Client.Data;

/// <summary>Records kept in memory for the session. Same behaviour as the future database: stamping, versions and soft delete.</summary>
public sealed class InMemoryRepository<T>(ICurrentContext context, TimeProvider time) : IRepository<T>
    where T : SyncEntity
{
    private readonly Dictionary<string, T> records = [];
    private readonly Lock gate = new();

    public event Action? Changed;

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

    public Task<T> SaveAsync(T entity, CancellationToken cancellationToken = default)
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

        Changed?.Invoke();
        return Task.FromResult(saved);
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var changed = false;
        lock (gate)
        {
            if (records.TryGetValue(id, out var existing) && !existing.Deleted)
            {
                records[id] = existing with
                {
                    Deleted = true,
                    UpdatedBy = context.UserId,
                    UpdatedAt = time.GetUtcNow(),
                    Version = existing.Version + 1,
                };
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return Task.CompletedTask;
    }
}
