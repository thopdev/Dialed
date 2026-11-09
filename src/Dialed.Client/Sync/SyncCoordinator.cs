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
}

/// <summary>
/// Decides when to sync and tells the UI how it is going. Syncs on start, when the app returns to the foreground, when the
/// network comes back and shortly after a local change (debounced), plus on request (pull-to-refresh, "Sync now").
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
    private readonly Lock gate = new();
    private CancellationTokenSource? pendingRequest;
    private CancellationTokenSource? retryTimer;
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
        TimeSpan? debounce = null)
    {
        this.engine = engine;
        this.state = state;
        this.tokens = tokens;
        this.connectivity = connectivity;
        this.lifecycle = lifecycle;
        this.time = time;
        this.debounce = debounce ?? TimeSpan.FromSeconds(3);
    }

    public SyncStatus Status { get; private set; } = SyncStatus.Idle;

    public SyncResult? LastResult { get; private set; }

    public DateTimeOffset? LastSyncedAt { get; private set; }

    /// <summary>Local changes not yet on the server (the "not yet synced" indicator).</summary>
    public int PendingCount { get; private set; }

    /// <summary>When the next automatic retry runs after the server could not be reached; <c>null</c> when none is planned.</summary>
    public DateTimeOffset? NextRetryAt { get; private set; }

    /// <summary>How long to wait before retry number <paramref name="failures"/> in a row: 5 s, 10 s, 20 s ... at most 5 minutes.</summary>
    public static TimeSpan BackoffDelay(int failures) =>
        TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Max(0, failures - 1))));

    /// <summary>Raised whenever anything above changes.</summary>
    public event Action? Changed;

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

    /// <summary>Syncs now. Returns without doing anything if a sync is already running.</summary>
    public async Task<SyncResult> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        if (Status == SyncStatus.Syncing)
        {
            return SyncResult.AlreadyRunning;
        }

        try
        {
            if (await tokens.GetAccessTokenAsync(cancellationToken) is null)
            {
                Update(SyncStatus.NeedsSignIn);
                await RefreshPendingAsync();
                return new SyncResult(SyncOutcome.Offline);
            }
        }
        catch (SyncTransportException)
        {
            // The sign-in could not be renewed right now (offline or server trouble): keep everything and try again later.
            Update(SyncStatus.Offline);
            ScheduleRetry();
            return new SyncResult(SyncOutcome.Offline);
        }

        if (!connectivity.IsOnline)
        {
            Update(SyncStatus.Offline);
            return new SyncResult(SyncOutcome.Offline);
        }

        Update(SyncStatus.Syncing);
        SyncResult result;
        try
        {
            result = await engine.SyncAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Update(SyncStatus.Idle);
            throw;
        }

        if (result.Outcome == SyncOutcome.AlreadyRunning)
        {
            return result;
        }

        LastResult = result;
        if (result.Outcome == SyncOutcome.Completed)
        {
            LastSyncedAt = time.GetUtcNow();
            StopRetrying();
        }
        else
        {
            ScheduleRetry();
        }

        await RefreshPendingAsync();
        Update(result.Outcome == SyncOutcome.Offline ? SyncStatus.Offline : SyncStatus.Idle);
        return result;
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
            retryTimer?.Cancel();
            retryTimer?.Dispose();
        }

        state.OutboxChanged -= OnOutboxChanged;
        connectivity.Changed -= OnConnectivityChanged;
        lifecycle.Resumed -= OnResumed;
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
        Changed?.Invoke();
    }

    private void Update(SyncStatus status)
    {
        Status = status;
        Changed?.Invoke();
    }
}
