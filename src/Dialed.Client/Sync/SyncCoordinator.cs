using Dialed.Client.Platform;

namespace Dialed.Client.Sync;

public enum SyncStatus
{
    /// <summary>Nothing running; <see cref="SyncCoordinator.LastResult"/> says how the last sync went.</summary>
    Idle,

    Syncing,

    /// <summary>The server could not be reached; changes wait on this device.</summary>
    Offline,

    /// <summary>Not signed in, so nothing is synced (everything stays on this device).</summary>
    NeedsSignIn,

    /// <summary>Another tab of the app is syncing; this one syncs when that is done (only one tab syncs at a time).</summary>
    SyncingElsewhere,
}

/// <summary>
/// Decides when to sync and tells the UI how it is going. Syncs on start, when the app returns to the foreground, when the
/// network comes back and shortly after a local change (debounced), plus on request (pull-to-refresh, "Sync now").
/// Only one tab syncs at a time (<see cref="ICrossTabLock"/>): a tab that finds another one syncing waits for it in the background
/// and then syncs itself.
/// </summary>
public sealed class SyncCoordinator : IDisposable
{
    private readonly SyncEngine engine;
    private readonly ISyncState state;
    private readonly IAccessTokenProvider tokens;
    private readonly IConnectivity connectivity;
    private readonly IAppLifecycle lifecycle;
    private readonly TimeProvider time;
    private readonly TimeSpan debounce;
    private readonly ICrossTabLock tabLock;
    private readonly Lock gate = new();
    private CancellationTokenSource? pendingRequest;
    private CancellationTokenSource? retryTimer;
    private CancellationTokenSource? otherTabWait;
    private int running;
    private int consecutiveFailures;
    private bool started;
    private bool disposed;

    public SyncCoordinator(
        SyncEngine engine,
        ISyncState state,
        IAccessTokenProvider tokens,
        IConnectivity connectivity,
        IAppLifecycle lifecycle,
        TimeProvider time,
        TimeSpan? debounce = null,
        ICrossTabLock? tabLock = null)
    {
        this.engine = engine;
        this.state = state;
        this.tokens = tokens;
        this.connectivity = connectivity;
        this.lifecycle = lifecycle;
        this.time = time;
        this.debounce = debounce ?? TimeSpan.FromSeconds(3);
        this.tabLock = tabLock ?? SingleWindowLock.Instance;
        engine.PullProgressed += OnPullProgressed;
    }

    public SyncStatus Status { get; private set; } = SyncStatus.Idle;

    public SyncResult? LastResult { get; private set; }

    public DateTimeOffset? LastSyncedAt { get; private set; }

    /// <summary>Local changes not yet on the server (the "not yet synced" indicator).</summary>
    public int PendingCount { get; private set; }

    /// <summary>
    /// While a first pull is running (new device, or the browser cleared the local database): how many records came in so far.
    /// 0 otherwise. The app shows "Loading your data from your account".
    /// </summary>
    public int RestoringCount { get; private set; }

    /// <summary>When the oldest change still waiting to be sent was made (since this launch); <c>null</c> when nothing waits.</summary>
    public DateTimeOffset? PendingSince { get; private set; }

    /// <summary>Changes waiting this long make the app warn that they are not safe on the server yet.</summary>
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(5);

    /// <summary>Changes have been waiting to be sent for more than <see cref="StuckAfter"/> while signed in.</summary>
    public bool HasStuckChanges => PendingCount > 0 && Status != SyncStatus.NeedsSignIn && PendingSince is { } since && time.GetUtcNow() - since >= StuckAfter;

    /// <summary>When the next automatic retry runs after the server could not be reached; <c>null</c> when none is planned.</summary>
    public DateTimeOffset? NextRetryAt { get; private set; }

    /// <summary>How long to wait before retry number <paramref name="failures"/> in a row: 5 s, 10 s, 20 s ... at most 5 minutes.</summary>
    public static TimeSpan BackoffDelay(int failures) =>
        TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Max(0, failures - 1))));

    /// <summary>The name of the lock every tab takes while it syncs.</summary>
    public const string LockName = "dialed-sync";

    /// <summary>Raised whenever anything above changes.</summary>
    public event Action? Changed;

    /// <summary>Raised when a sync ended with changes that could not be sent (for error reporting).</summary>
    public event Action<SyncResult>? SyncFailed;

    /// <summary>Starts listening for triggers and does the first sync. Call once, after the platform services started.</summary>
    public async Task StartAsync()
    {
        lock (gate)
        {
            if (started || disposed)
            {
                return;
            }

            started = true;
        }

        state.OutboxChanged += OnOutboxChanged;
        connectivity.Changed += OnConnectivityChanged;
        lifecycle.Resumed += OnResumed;
        await RefreshPendingAsync();
        await SyncNowAsync();
    }

    /// <summary>
    /// Syncs now. Returns without doing anything if a sync is already running, here or in another tab (this tab then syncs once
    /// the other one is done).
    /// </summary>
    public Task<SyncResult> SyncNowAsync(CancellationToken cancellationToken = default) => SyncAsync(heldLock: null, cancellationToken);

    /// <summary>
    /// One sync at a time in this tab (<see cref="running"/>), and across tabs with the cross-tab lock: <paramref name="heldLock"/>
    /// when the caller already took it (after waiting for another tab), otherwise taken here.
    /// </summary>
    private async Task<SyncResult> SyncAsync(IAsyncDisposable? heldLock, CancellationToken cancellationToken)
    {
        if ((heldLock is null && IsWaitingForOtherTab) || Interlocked.CompareExchange(ref running, 1, 0) != 0)
        {
            if (heldLock is not null)
            {
                await heldLock.DisposeAsync();
            }

            return SyncResult.AlreadyRunning;
        }

        SyncResult result;
        bool otherTabSyncing;
        try
        {
            (result, otherTabSyncing) = await SyncHereAsync(heldLock, cancellationToken);
        }
        finally
        {
            if (heldLock is not null)
            {
                await heldLock.DisposeAsync();
            }

            Volatile.Write(ref running, 0);
        }

        // Only after this sync stopped counting as running, so the wait can start the next one.
        if (otherTabSyncing)
        {
            WaitForOtherTab();
        }

        return result;
    }

    private async Task<(SyncResult Result, bool OtherTabSyncing)> SyncHereAsync(IAsyncDisposable? heldLock, CancellationToken cancellationToken)
    {
        try
        {
            if (await tokens.GetAccessTokenAsync(cancellationToken) is null)
            {
                Update(SyncStatus.NeedsSignIn);
                await RefreshPendingAsync();
                return (new SyncResult(SyncOutcome.Offline), false);
            }
        }
        catch (SyncTransportException)
        {
            // The sign-in could not be renewed right now (offline or server trouble): keep everything and try again later.
            Update(SyncStatus.Offline);
            ScheduleRetry();
            return (new SyncResult(SyncOutcome.Offline), false);
        }

        if (!connectivity.IsOnline)
        {
            Update(SyncStatus.Offline);
            return (new SyncResult(SyncOutcome.Offline), false);
        }

        // A lock taken here is freed when this method ends; one handed in is freed by the caller.
        await using var ownLock = heldLock is null ? await tabLock.TryAcquireAsync(LockName, cancellationToken) : null;
        if (heldLock is null && ownLock is null)
        {
            return (SyncResult.AlreadyRunning, true);
        }

        Update(SyncStatus.Syncing);
        SyncResult result;
        try
        {
            // Removed from a household (or moved by another device): the token now names another one, so tidy up before syncing.
            if (tokens.HouseholdId is { } householdId)
            {
                await engine.ReconcileHouseholdAsync(householdId, cancellationToken);
            }

            result = await engine.SyncAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Update(SyncStatus.Idle);
            throw;
        }

        if (result.Outcome == SyncOutcome.AlreadyRunning)
        {
            return (result, false);
        }

        LastResult = result;
        if (result.Failed > 0)
        {
            SyncFailed?.Invoke(result);
        }

        if (result.Outcome == SyncOutcome.Completed)
        {
            LastSyncedAt = time.GetUtcNow();
            StopRetrying();
        }
        else
        {
            ScheduleRetry();
        }

        RestoringCount = 0;
        await RefreshPendingAsync();
        Update(result.Outcome == SyncOutcome.Offline ? SyncStatus.Offline : SyncStatus.Idle);
        return (result, false);
    }

    /// <summary>Syncs after the debounce time; several calls in a row become one sync.</summary>
    public void RequestSync()
    {
        CancellationTokenSource request;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            pendingRequest?.Cancel();
            pendingRequest?.Dispose();
            request = pendingRequest = new CancellationTokenSource();
        }

        _ = RunAfterDebounceAsync(request.Token);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            pendingRequest?.Cancel();
            pendingRequest?.Dispose();
            otherTabWait?.Cancel();
            otherTabWait?.Dispose();
            otherTabWait = null;
            retryTimer?.Cancel();
            retryTimer?.Dispose();
        }

        state.OutboxChanged -= OnOutboxChanged;
        engine.PullProgressed -= OnPullProgressed;
        connectivity.Changed -= OnConnectivityChanged;
        lifecycle.Resumed -= OnResumed;
    }

    private bool IsWaitingForOtherTab
    {
        get
        {
            lock (gate)
            {
                return otherTabWait is not null;
            }
        }
    }

    /// <summary>Another tab holds the sync lock: say so, wait until it is done, then sync here (its changes may be ours to pull).</summary>
    private void WaitForOtherTab()
    {
        CancellationTokenSource wait;
        lock (gate)
        {
            if (disposed || otherTabWait is not null)
            {
                return;
            }

            wait = otherTabWait = new CancellationTokenSource();
        }

        Update(SyncStatus.SyncingElsewhere);
        _ = WaitForOtherTabAsync(wait);
    }

    private async Task WaitForOtherTabAsync(CancellationTokenSource wait)
    {
        IAsyncDisposable? held = null;
        try
        {
            held = await tabLock.AcquireAsync(LockName, wait.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // The lock itself failed; sync anyway below rather than stay stuck waiting.
        }

        bool current;
        lock (gate)
        {
            current = otherTabWait == wait;
            if (current)
            {
                otherTabWait = null;
                wait.Dispose();
            }
        }

        if (!current)
        {
            // Disposed meanwhile.
            if (held is not null)
            {
                await held.DisposeAsync();
            }

            return;
        }

        held ??= await SingleWindowLock.Instance.AcquireAsync(LockName);
        Update(SyncStatus.Idle);
        try
        {
            // The other tab is done. Sync here while still holding the lock: freeing it and taking it again could find it not yet
            // freed and mistake this tab for another one.
            await SyncAsync(held, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The server could not be reached: try again after an exponentially growing wait, so a flaky connection is not hammered.</summary>
    private void ScheduleRetry()
    {
        CancellationTokenSource timer;
        TimeSpan delay;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            retryTimer?.Cancel();
            retryTimer?.Dispose();
            timer = retryTimer = new CancellationTokenSource();
            consecutiveFailures++;
            delay = BackoffDelay(consecutiveFailures);
            NextRetryAt = time.GetUtcNow() + delay;
        }

        _ = RetryAfterAsync(delay, timer.Token);
    }

    private void StopRetrying()
    {
        lock (gate)
        {
            consecutiveFailures = 0;
            NextRetryAt = null;
            retryTimer?.Cancel();
            retryTimer?.Dispose();
            retryTimer = null;
        }
    }

    private async Task RetryAfterAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, time, cancellationToken);
            await SyncNowAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunAfterDebounceAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (debounce > TimeSpan.Zero)
            {
                await Task.Delay(debounce, time, cancellationToken);
            }

            await SyncNowAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnOutboxChanged()
    {
        _ = RefreshPendingAsync();
        RequestSync();
    }

    private void OnConnectivityChanged(bool online)
    {
        if (online)
        {
            RequestSync();
        }
        else
        {
            Update(SyncStatus.Offline);
        }
    }

    private void OnResumed() => RequestSync();

    private async Task RefreshPendingAsync()
    {
        PendingCount = await engine.PendingCountAsync();

        // The clock for "waiting too long" starts when something starts waiting, and stops when nothing waits.
        PendingSince = PendingCount == 0 ? null : PendingSince ?? time.GetUtcNow();
        Changed?.Invoke();
    }

    private void OnPullProgressed(PullProgress progress)
    {
        if (progress.FirstPull && progress.Applied > 0)
        {
            RestoringCount = progress.Applied;
            Changed?.Invoke();
        }
    }

    private void Update(SyncStatus status)
    {
        Status = status;
        Changed?.Invoke();
    }
}
