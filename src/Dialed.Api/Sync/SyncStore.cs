using System.Text.Json.Nodes;

namespace Dialed.Api.Sync;

/// <summary>A synced record as the server holds it: the JSON the app sent plus the server's own bookkeeping.</summary>
public sealed record SyncRow(string Id, string HouseholdId, DateTimeOffset UpdatedAt, int Version, JsonObject Entity);

public enum StoreOutcome { Stored, VersionMismatch, Forbidden, Invalid }

public sealed record StoreResult(StoreOutcome Outcome, SyncRow? Row = null, string? Message = null);

/// <summary>Rows per table, always scoped to one household. In memory for now; Cosmos DB (one container, partition <c>/householdId</c>) replaces it.</summary>
public interface ISyncStore
{
    /// <summary>Rows changed after the watermark, ordered by update time then id, deleted ones included.</summary>
    Task<(IReadOnlyList<SyncRow> Rows, bool HasMore)> PullAsync(string householdId, string table, DateTimeOffset? afterUpdatedAt, string? afterId, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates one row. <paramref name="ifMatch"/> is the version the edit started from (<c>null</c> for a new record).</summary>
    Task<StoreResult> PushAsync(string householdId, string userId, string table, string id, JsonObject entity, string? ifMatch, CancellationToken cancellationToken = default);
}

public sealed class InMemorySyncStore(TimeProvider time) : ISyncStore
{
    private readonly Lock gate = new();

    // Ids are only unique within a household, so the household is part of the key (it is the partition in Cosmos DB).
    private readonly Dictionary<(string Household, string Table, string Id), SyncRow> rows = [];

    public Task<(IReadOnlyList<SyncRow> Rows, bool HasMore)> PullAsync(string householdId, string table, DateTimeOffset? afterUpdatedAt, string? afterId, int pageSize, CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 500);
        lock (gate)
        {
            var matching = rows
                .Where(r => r.Key.Household == householdId && r.Key.Table == table)
                .Select(r => r.Value)
                .Where(r => afterUpdatedAt is null
                    || r.UpdatedAt > afterUpdatedAt
                    || (r.UpdatedAt == afterUpdatedAt && string.CompareOrdinal(r.Id, afterId ?? "") > 0))
                .OrderBy(r => r.UpdatedAt)
                .ThenBy(r => r.Id, StringComparer.Ordinal)
                .Take(pageSize + 1)
                .ToList();

            return Task.FromResult<(IReadOnlyList<SyncRow>, bool)>((matching.Take(pageSize).ToList(), matching.Count > pageSize));
        }
    }

    public Task<StoreResult> PushAsync(string householdId, string userId, string table, string id, JsonObject entity, string? ifMatch, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || entity["id"]?.GetValue<string>() is not { } bodyId || bodyId != id)
        {
            return Task.FromResult(new StoreResult(StoreOutcome.Invalid, Message: "The record id must match the URL."));
        }

        // A record that names another household is refused rather than quietly moved into the caller's.
        if (entity["householdId"]?.GetValue<string>() is { Length: > 0 } claimed && claimed != householdId)
        {
            return Task.FromResult(new StoreResult(StoreOutcome.Forbidden, Message: "No access to that household."));
        }

        lock (gate)
        {
            var key = (householdId, table, id);
            var exists = rows.TryGetValue(key, out var current);

            // The same id in another household is a different record, but we never reveal that one exists.
            if (exists && ifMatch != current!.Version.ToString())
            {
                return Task.FromResult(new StoreResult(StoreOutcome.VersionMismatch, current));
            }

            if (!exists && ifMatch is not null)
            {
                return Task.FromResult(new StoreResult(StoreOutcome.Invalid, Message: "Unknown version."));
            }

            var now = time.GetUtcNow();
            var version = exists ? current!.Version + 1 : 1;
            var stored = (JsonObject)entity.DeepClone();
            stored["householdId"] = householdId;
            stored["updatedBy"] = userId;
            stored["updatedAt"] = now;
            stored["version"] = version;
            if (!exists)
            {
                stored["createdBy"] = userId;
                stored["createdAt"] = now;
            }
            else
            {
                // Creation details never change.
                stored["createdBy"] = current!.Entity["createdBy"]?.DeepClone();
                stored["createdAt"] = current.Entity["createdAt"]?.DeepClone();
            }

            var row = new SyncRow(id, householdId, now, version, stored);
            rows[key] = row;
            return Task.FromResult(new StoreResult(StoreOutcome.Stored, row));
        }
    }
}
