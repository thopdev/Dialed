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

    /// <summary>Local changes the server refused or that keep failing, with the reason; they stay queued until retried or discarded.</summary>
    public async Task<IReadOnlyList<OutboxEntry>> FailedAsync(CancellationToken cancellationToken = default) =>
        [.. (await state.PendingAsync(cancellationToken)).Where(e => e.LastError is not null)];

    /// <summary>Forgets the failure so the next sync tries the change again.</summary>
    public Task RetryAsync(long sequence, CancellationToken cancellationToken = default) => state.ResetFailureAsync(sequence, cancellationToken);

    /// <summary>Stops trying to send this change. The record stays on this device but the server never gets this version.</summary>
    public Task DiscardAsync(long sequence, CancellationToken cancellationToken = default) => state.RemoveAsync(sequence, cancellationToken);

    /// <summary>After sign-in: records made on this device before it belonged to a household move into the person's household and are queued.</summary>
    public async Task<int> AdoptLocalRecordsAsync(CancellationToken cancellationToken = default)
    {
        var adopted = 0;
        foreach (var table in byName.Values)
        {
            adopted += await table.AdoptLocalRecordsAsync(cancellationToken);
        }

        return adopted;
    }

    /// <summary>
    /// Makes this device hold only the data of <paramref name="householdId"/>. When it differs from the household last synced for,
    /// the old household's records are removed from this device (they stay on the server), positions and versions are forgotten
    /// and the new household is pulled in full. Returns <c>true</c> when the household had changed.
    /// </summary>
    public async Task<bool> ReconcileHouseholdAsync(string householdId, CancellationToken cancellationToken = default)
    {
        var known = await state.GetHouseholdAsync(cancellationToken);
        if (known == householdId)
        {
            return false;
        }

        if (known is null || householdId == Data.LocalContext.LocalHouseholdId)
        {
            // First sync on this device, or signed out: nothing to move, and nothing is wiped on sign-out.
            await state.SetHouseholdAsync(householdId, cancellationToken);
            return false;
        }

        await SwitchHouseholdAsync(householdId, cancellationToken);
        return true;
    }

    /// <summary>The person joined, left, was removed or switched: forget the old household's positions and records, pull the new household, and hand records made before sign-in to it.</summary>
    public async Task SwitchHouseholdAsync(string householdId, CancellationToken cancellationToken = default)
    {
        await state.SetHouseholdAsync(householdId, cancellationToken);
        await state.ResetServerStateAsync(cancellationToken);
        foreach (var table in byName.Values)
        {
            await table.PurgeForeignRecordsAsync(cancellationToken);
        }

        await AdoptLocalRecordsAsync(cancellationToken);
    }

    /// <summary>Raised after each page pulled from the server, with the running total of records applied in this sync.</summary>
    public event Action<PullProgress>? PullProgressed;

    private async Task<bool> IsFirstPullAsync(CancellationToken cancellationToken)
    {
        foreach (var name in byName.Keys)
        {
            if (await state.GetWatermarkAsync(name, cancellationToken) is not null)
            {
                return false;
            }
        }

        return true;
    }

    public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(0, cancellationToken))
        {
            return SyncResult.AlreadyRunning;
        }

        int pushed = 0, conflicts = 0, failed = 0, pulled = 0;
        try
        {
            // Nothing pulled yet for any table: a new device, or the browser cleared the local database (#114).
            var firstPull = await IsFirstPullAsync(cancellationToken);
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
                var before = pulled;
                pulled += await table.PullAsync(pageSize, cancellationToken, applied => PullProgressed?.Invoke(new PullProgress(firstPull, before + applied)));
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
