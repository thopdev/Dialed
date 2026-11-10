using System.Text.Json;
using Dialed.Domain.Model;

namespace Dialed.Client.Sync;

/// <summary>One synced table, without its record type, so the engine can loop over all of them.</summary>
public interface ISyncTable
{
    string Name { get; }

    /// <summary>Pushes one outbox entry. Returns what happened so the engine can count and decide.</summary>
    Task<PushStep> PushAsync(OutboxEntry entry, CancellationToken cancellationToken);

    /// <summary>Pulls every page after the stored watermark and returns how many records were applied.</summary>
    /// <param name="onPage">Called after each page with how many records this table has applied so far.</param>
    Task<int> PullAsync(int pageSize, CancellationToken cancellationToken, Action<int>? onPage = null);

    /// <summary>Hands records made before sign-in to the current household and queues them for pushing.</summary>
    Task<int> AdoptLocalRecordsAsync(CancellationToken cancellationToken);

    /// <summary>Removes what belonged to a household the person is no longer in (see <see cref="ISyncRepository{T}.PurgeForeignAsync"/>).</summary>
    Task<int> PurgeForeignRecordsAsync(CancellationToken cancellationToken);
}

public enum PushStep
{
    /// <summary>Done: pushed, or nothing left to push.</summary>
    Pushed,

    /// <summary>Done, but only after this device overwrote a newer server copy.</summary>
    PushedOverConflict,

    /// <summary>The server refused it or it kept conflicting; the entry stays in the outbox.</summary>
    Failed,
}

/// <summary>
/// Sync of one record type. Brews are only ever added (and edited by their author) and inventory is derived, so a plain
/// "last writer wins, keep a log of what was overwritten" rule is enough for every table (ADR-0011).
/// </summary>
public sealed class SyncTable<T>(string name, ISyncRepository<T> repository, ISyncState state, ISyncTransport transport, TimeProvider time) : ISyncTable
    where T : SyncEntity
{
    private const int MaxConflictRetries = 3;

    public string Name { get; } = name;

    /// <summary>Starts putting local saves and deletes into the outbox. Call once.</summary>
    public SyncTable<T> Attach()
    {
        repository.LocalChanged += entity => _ = state.EnqueueAsync(Name, entity.Id);
        return this;
    }

    public async Task<PushStep> PushAsync(OutboxEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var step = PushStep.Pushed;
        for (var attempt = 0; attempt < MaxConflictRetries; attempt++)
        {
            var record = await repository.GetIncludingDeletedAsync(entry.EntityId, cancellationToken);
            if (record is null)
            {
                await state.RemoveAsync(entry.Sequence, cancellationToken);
                return step;
            }

            var version = await state.GetServerVersionAsync(Name, entry.EntityId, cancellationToken);
            var result = await transport.PushAsync(Name, record, version, cancellationToken);
            switch (result.Outcome)
            {
                case PushOutcome.Accepted:
                    await state.SetServerVersionAsync(Name, entry.EntityId, result.Record!.Version, cancellationToken);
                    await state.RemoveAsync(entry.Sequence, cancellationToken);
                    return step;

                case PushOutcome.Conflict when result.Record is null:
                    await state.RecordFailureAsync(entry.Sequence, "The server reported a conflict without its copy.", cancellationToken);
                    return PushStep.Failed;

                case PushOutcome.Conflict:
                    // Last writer wins: keep what the server had in the log, then send our copy on top of the server's version.
                    var serverCopy = result.Record!;
                    await state.AddConflictAsync(
                        new ConflictLogEntry(Name, entry.EntityId, time.GetUtcNow(), JsonSerializer.Serialize(serverCopy.Entity)),
                        cancellationToken);
                    await state.SetServerVersionAsync(Name, entry.EntityId, serverCopy.Version, cancellationToken);
                    step = PushStep.PushedOverConflict;
                    break;

                default:
                    await state.RecordFailureAsync(entry.Sequence, result.Message ?? "Rejected by the server.", cancellationToken);
                    return PushStep.Failed;
            }
        }

        await state.RecordFailureAsync(entry.Sequence, "The record kept changing on the server.", cancellationToken);
        return PushStep.Failed;
    }

    public Task<int> AdoptLocalRecordsAsync(CancellationToken cancellationToken) => repository.AdoptAsync(cancellationToken);

    public Task<int> PurgeForeignRecordsAsync(CancellationToken cancellationToken) => repository.PurgeForeignAsync(cancellationToken);

    public async Task<int> PullAsync(int pageSize, CancellationToken cancellationToken, Action<int>? onPage = null)
    {
        var applied = 0;
        var watermark = await state.GetWatermarkAsync(Name, cancellationToken);
        bool hasMore;
        do
        {
            var page = await transport.PullAsync<T>(Name, watermark, pageSize, cancellationToken);
            foreach (var server in page.Records)
            {
                var entity = server.Entity;
                await state.SetServerVersionAsync(Name, entity.Id, server.Version, cancellationToken);

                // A change made on this device that is not pushed yet wins; it goes out on top of this version.
                if (!await state.HasPendingAsync(Name, entity.Id, cancellationToken))
                {
                    await repository.ApplyRemoteAsync(entity, cancellationToken);
                    applied++;
                }

                watermark = new SyncWatermark(entity.UpdatedAt, entity.Id);
            }

            if (page.Records.Count > 0)
            {
                await state.SetWatermarkAsync(Name, watermark!, cancellationToken);
                onPage?.Invoke(applied);
            }

            hasMore = page.HasMore && page.Records.Count > 0;
        }
        while (hasMore);

        return applied;
    }
}
