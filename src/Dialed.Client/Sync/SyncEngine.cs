namespace Dialed.Client.Sync;

/// <summary>
/// Pushes the outbox, then pulls every table. One sync at a time; a failed network leaves the outbox as it was.
/// Triggers (app start, back to the foreground, connectivity regained, after changes, pull-to-refresh) call <see cref="SyncAsync"/>.
/// </summary>
public sealed class SyncEngine(IEnumerable<ISyncTable> tables, ISyncState state, int pageSize = SyncEngine.DefaultPageSize)
{
    public const int DefaultPageSize = 100;

    private readonly Dictionary<string, ISyncTable> byName = tables.ToDictionary(t => t.Name, StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>How many local changes are waiting to be pushed (for the "not yet synced" indicator).</summary>
    public async Task<int> PendingCountAsync(CancellationToken cancellationToken = default) =>
        (await state.PendingAsync(cancellationToken)).Count;

    public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(0, cancellationToken))
        {
            return SyncResult.AlreadyRunning;
        }

        int pushed = 0, conflicts = 0, failed = 0, pulled = 0;
        try
        {
            foreach (var entry in await state.PendingAsync(cancellationToken))
            {
                if (!byName.TryGetValue(entry.Table, out var table))
                {
                    continue;
                }

                switch (await table.PushAsync(entry, cancellationToken))
                {
                    case PushStep.Pushed:
                        pushed++;
                        break;
                    case PushStep.PushedOverConflict:
                        pushed++;
                        conflicts++;
                        break;
                    default:
                        failed++;
                        break;
                }
            }

            foreach (var table in byName.Values)
            {
                pulled += await table.PullAsync(pageSize, cancellationToken);
            }

            return new SyncResult(SyncOutcome.Completed, pushed, pulled, conflicts, failed);
        }
        catch (SyncTransportException)
        {
            return new SyncResult(SyncOutcome.Offline, pushed, pulled, conflicts, failed);
        }
        finally
        {
            gate.Release();
        }
    }
}
