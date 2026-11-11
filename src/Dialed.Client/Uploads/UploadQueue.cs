using System.Text.Json;
using Dialed.Client.Data;
using Dialed.Client.Platform;
using Dialed.Client.Storage;
using Dialed.Client.Sync;
using Dialed.Domain.Model;

namespace Dialed.Client.Uploads;

public enum UploadStatus
{
    /// <summary>On this device, waiting for its turn, a network, a sign-in or the next retry.</summary>
    Waiting,

    Uploading,

    /// <summary>Gave up after repeated failures, or the photo was refused. Only a manual retry starts it again.</summary>
    Failed,
}

/// <summary>One photo in the queue, as the UI sees it.</summary>
public sealed record UploadItem(string PhotoId, UploadStatus Status, int Attempts, DateTimeOffset? NextAttemptAt, string? LastError);

/// <summary>
/// Photo files waiting to go to storage (#68). Survives restarts (kept in the <see cref="ILocalStore"/>, the files in the
/// <see cref="ILocalFileStore"/>), uploads when there is a network and a sign-in, and retries with a growing wait.
/// </summary>
public interface IUploadQueue
{
    /// <summary>Everything in the queue, oldest first.</summary>
    IReadOnlyList<UploadItem> Items { get; }

    /// <summary>Photos still to upload (waiting or uploading).</summary>
    int PendingCount { get; }

    /// <summary>Photos the queue gave up on.</summary>
    int FailedCount { get; }

    /// <summary>The photo's place in the queue, or <c>null</c> when it is not in it (uploaded, or never on this device).</summary>
    UploadStatus? StatusOf(string photoId);

    /// <summary>Adds a photo whose file is in the local file store and starts uploading when possible.</summary>
    Task EnqueueAsync(string photoId, CancellationToken cancellationToken = default);

    /// <summary>Takes a photo out of the queue (it was deleted).</summary>
    Task RemoveAsync(string photoId, CancellationToken cancellationToken = default);

    /// <summary>Starts failed uploads again: one photo, or all failed ones when <paramref name="photoId"/> is <c>null</c>.</summary>
    Task RetryAsync(string? photoId = null, CancellationToken cancellationToken = default);

    /// <summary>Raised when anything above changes.</summary>
    event Action? Changed;
}

/// <summary>
/// The upload queue. One upload at a time (photos are about 0.5 MB and phones are on mobile data). Every attempt asks the API for a fresh
/// short-lived upload URL, then sends the file there. Being offline or signed out never counts as a failure; server trouble does, and
/// after <see cref="MaxFailures"/> of those the photo is marked failed until the person retries it.
/// </summary>
public sealed class PhotoUploadQueue : IUploadQueue, ILoadable, IDisposable
{
    public const string Collection = "uploads";

    /// <summary>Server errors in a row before the queue gives up on a photo (about an hour with <see cref="BackoffDelay"/>).</summary>
    public const int MaxFailures = 8;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ILocalStore store;
    private readonly ILocalFileStore files;
    private readonly IRepository<Photo> photos;
    private readonly IPhotoUploadApi api;
    private readonly IConnectivity connectivity;
    private readonly IAppLifecycle lifecycle;
    private readonly TimeProvider time;
    private readonly SyncCoordinator? sync;
    private readonly Lock gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private CancellationTokenSource? wake;
    private DateTimeOffset? lastSyncSeen;
    private string? uploading;
    private bool running;
    private bool runAgain;
    private bool started;
    private bool disposed;

    public PhotoUploadQueue(
        ILocalStore store,
        ILocalFileStore files,
        IRepository<Photo> photos,
        IPhotoUploadApi api,
        IConnectivity connectivity,
        IAppLifecycle lifecycle,
        TimeProvider time,
        SyncCoordinator? sync = null)
    {
        this.store = store;
        this.files = files;
        this.photos = photos;
        this.api = api;
        this.connectivity = connectivity;
        this.lifecycle = lifecycle;
        this.time = time;
        this.sync = sync;
    }

    /// <summary>What is kept per photo in the local store.</summary>
    private sealed record Entry(
        string PhotoId,
        DateTimeOffset QueuedAt,
        int Attempts = 0,
        int Failures = 0,
        DateTimeOffset? NextAttemptAt = null,
        UploadProblem? LastProblem = null,
        bool Failed = false,
        string? LastError = null);

    public event Action? Changed;

    public IReadOnlyList<UploadItem> Items
    {
        get
        {
            lock (gate)
            {
                return [.. entries.Values.OrderBy(e => e.QueuedAt).ThenBy(e => e.PhotoId, StringComparer.Ordinal).Select(ToItem)];
            }
        }
    }

    public int PendingCount
    {
        get
        {
            lock (gate)
            {
                return entries.Values.Count(e => !e.Failed);
            }
        }
    }

    public int FailedCount
    {
        get
        {
            lock (gate)
            {
                return entries.Values.Count(e => e.Failed);
            }
        }
    }

    /// <summary>How long to wait before attempt number <paramref name="attempts"/> + 1: 10 s, 20 s, 40 s ... at most 30 minutes.</summary>
    public static TimeSpan BackoffDelay(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(1800, 10 * Math.Pow(2, Math.Max(0, attempts - 1))));

    public UploadStatus? StatusOf(string photoId)
    {
        lock (gate)
        {
            return entries.TryGetValue(photoId, out var entry) ? ToItem(entry).Status : null;
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var documents = await store.ReadAllAsync(Collection, cancellationToken);
        lock (gate)
        {
            entries.Clear();
            foreach (var json in documents.Values)
            {
                if (Deserialize(json) is { } entry)
                {
                    entries[entry.PhotoId] = entry;
                }
            }
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Starts listening for a network, the app coming back and finished syncs, picks up photos taken before the queue existed, and starts
    /// uploading. Call once after <see cref="LoadAsync"/> and after the platform services started.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (started || disposed)
            {
                return;
            }

            started = true;
        }

        connectivity.Changed += OnConnectivityChanged;
        lifecycle.Resumed += Kick;
        if (sync is not null)
        {
            lastSyncSeen = sync.LastSyncedAt;
            sync.Changed += OnSyncChanged;
        }

        await AdoptWaitingPhotosAsync(cancellationToken);
        Kick();
    }

    public async Task EnqueueAsync(string photoId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(photoId);

        var entry = new Entry(photoId, time.GetUtcNow());
        lock (gate)
        {
            if (entries.ContainsKey(photoId))
            {
                return;
            }

            entries[photoId] = entry;
        }

        await PersistAsync(entry, cancellationToken);
        Changed?.Invoke();
        Kick();
    }

    public async Task RemoveAsync(string photoId, CancellationToken cancellationToken = default)
    {
        bool removed;
        lock (gate)
        {
            removed = entries.Remove(photoId);
        }

        if (removed)
        {
            await store.RemoveAsync(Collection, photoId, cancellationToken);
            Changed?.Invoke();
        }
    }

    public async Task RetryAsync(string? photoId = null, CancellationToken cancellationToken = default)
    {
        List<Entry> reset;
        lock (gate)
        {
            reset = [.. entries.Values
                .Where(e => e.Failed && (photoId is null || e.PhotoId == photoId))
                .Select(e => e with { Failed = false, Attempts = 0, Failures = 0, NextAttemptAt = null, LastProblem = null, LastError = null })];
            foreach (var entry in reset)
            {
                entries[entry.PhotoId] = entry;
            }
        }

        foreach (var entry in reset)
        {
            await PersistAsync(entry, cancellationToken);
            await SetPhotoStateAsync(entry.PhotoId, UploadState.Pending, cancellationToken);
        }

        Changed?.Invoke();
        Kick();
    }

    /// <summary>Uploads every photo that is due now, one after the other. Safe to call at any time; a call while running makes it look again.</summary>
    public async Task ProcessAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            if (running)
            {
                runAgain = true;
                return;
            }

            running = true;
        }

        try
        {
            do
            {
                lock (gate)
                {
                    runAgain = false;
                }

                while (connectivity.IsOnline && NextDue() is { } entry)
                {
                    if (!await UploadOneAsync(entry, cancellationToken))
                    {
                        break; // signed out: nothing else will work either
                    }
                }
            }
            while (RunAgain());
        }
        finally
        {
            lock (gate)
            {
                running = false;
                uploading = null;
            }

            ScheduleWake();
            Changed?.Invoke();
        }
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
            wake?.Cancel();
            wake?.Dispose();
            wake = null;
        }

        connectivity.Changed -= OnConnectivityChanged;
        lifecycle.Resumed -= Kick;
        if (sync is not null)
        {
            sync.Changed -= OnSyncChanged;
        }
    }

    /// <returns><c>false</c> when uploading should stop for now (no sign-in).</returns>
    private async Task<bool> UploadOneAsync(Entry entry, CancellationToken cancellationToken)
    {
        var photo = await photos.GetAsync(entry.PhotoId, cancellationToken);
        if (photo is null || photo.UploadState == UploadState.Uploaded)
        {
            await RemoveAsync(entry.PhotoId, cancellationToken); // deleted in the meantime, or uploaded by an earlier run
            return true;
        }

        var content = await files.ReadAsync(photo.LocalPath, cancellationToken);
        if (content is null)
        {
            // The browser cleared the app's storage before the upload (iOS can do this): the photo is gone, nothing to retry.
            await SetPhotoStateAsync(entry.PhotoId, UploadState.Failed, cancellationToken);
            await RemoveAsync(entry.PhotoId, cancellationToken);
            return true;
        }

        lock (gate)
        {
            uploading = entry.PhotoId;
        }

        Changed?.Invoke();
        try
        {
            var target = await api.GetUploadUrlAsync(entry.PhotoId, cancellationToken);
            await api.UploadAsync(target, content, photo.ContentType, cancellationToken);
        }
        catch (PhotoUploadException exception)
        {
            return await HandleProblemAsync(entry, exception.Problem, exception.Message, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await HandleProblemAsync(entry, UploadProblem.Transient, exception.Message, cancellationToken);
        }
        finally
        {
            lock (gate)
            {
                uploading = null;
            }
        }

        await SetPhotoStateAsync(entry.PhotoId, UploadState.Uploaded, cancellationToken);
        await RemoveAsync(entry.PhotoId, cancellationToken);
        return true;
    }

    private async Task<bool> HandleProblemAsync(Entry entry, UploadProblem problem, string message, CancellationToken cancellationToken)
    {
        if (problem == UploadProblem.SignedOut)
        {
            return false; // waits for the next sign-in, sync or return to the app; not an attempt
        }

        var attempts = entry.Attempts + 1;
        var failures = entry.Failures + (problem is UploadProblem.Transient ? 1 : 0);
        var failed = problem == UploadProblem.Rejected || failures >= MaxFailures;
        var next = entry with
        {
            Attempts = attempts,
            Failures = failures,
            Failed = failed,
            LastProblem = problem,
            NextAttemptAt = failed ? null : time.GetUtcNow() + BackoffDelay(attempts),
            LastError = message,
        };

        lock (gate)
        {
            if (!entries.ContainsKey(entry.PhotoId))
            {
                return true; // removed while uploading
            }

            entries[entry.PhotoId] = next;
        }

        await PersistAsync(next, cancellationToken);
        if (failed)
        {
            await SetPhotoStateAsync(entry.PhotoId, UploadState.Failed, cancellationToken);
        }

        Changed?.Invoke();

        // Offline: the rest will not get through either; wait for the network or the retry.
        return problem != UploadProblem.Offline || connectivity.IsOnline;
    }

    private Entry? NextDue()
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            return entries.Values
                .Where(e => !e.Failed && (e.NextAttemptAt is null || e.NextAttemptAt <= now))
                .OrderBy(e => e.QueuedAt)
                .ThenBy(e => e.PhotoId, StringComparer.Ordinal)
                .FirstOrDefault();
        }
    }

    private bool RunAgain()
    {
        lock (gate)
        {
            return runAgain && !disposed;
        }
    }

    /// <summary>Photos still marked pending whose file is on this device but that are not queued (taken before the queue existed).</summary>
    private async Task AdoptWaitingPhotosAsync(CancellationToken cancellationToken)
    {
        foreach (var photo in await photos.ListAsync(cancellationToken))
        {
            bool queued;
            lock (gate)
            {
                queued = entries.ContainsKey(photo.Id);
            }

            if (photo.UploadState == UploadState.Pending && !queued && await files.ExistsAsync(photo.LocalPath, cancellationToken))
            {
                await EnqueueAsync(photo.Id, cancellationToken);
            }
        }
    }

    private async Task SetPhotoStateAsync(string photoId, UploadState state, CancellationToken cancellationToken)
    {
        // Read the latest copy first: sync may have changed the record since the upload started.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var photo = await photos.GetAsync(photoId, cancellationToken);
            if (photo is null || photo.UploadState == state)
            {
                return;
            }

            try
            {
                await photos.SaveAsync(photo with { UploadState = state }, cancellationToken);
                return;
            }
            catch (ConflictException)
            {
            }
        }
    }

    private void ScheduleWake()
    {
        CancellationTokenSource timer;
        TimeSpan delay;
        lock (gate)
        {
            wake?.Cancel();
            wake?.Dispose();
            wake = null;
            var next = entries.Values.Where(e => !e.Failed && e.NextAttemptAt is not null).Min(e => e.NextAttemptAt);
            if (disposed || next is null)
            {
                return;
            }

            delay = next.Value - time.GetUtcNow();
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            timer = wake = new CancellationTokenSource();
        }

        _ = WakeAfterAsync(delay, timer.Token);
    }

    private async Task WakeAfterAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, time, cancellationToken);
            await ProcessAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Kick() => _ = RunInBackgroundAsync();

    private async Task RunInBackgroundAsync()
    {
        try
        {
            await ProcessAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A broken local store must not take the app down; the next trigger tries again.
            Console.Error.WriteLine($"Photo uploads stopped: {exception.Message}");
        }
    }

    private void OnConnectivityChanged(bool online)
    {
        if (online)
        {
            // Back online: what waited only for the network can go now.
            lock (gate)
            {
                foreach (var entry in entries.Values.Where(e => !e.Failed && e.NextAttemptAt is not null && e.LastProblem == UploadProblem.Offline).ToList())
                {
                    entries[entry.PhotoId] = entry with { NextAttemptAt = null };
                }
            }

            Kick();
        }
    }

    private void OnSyncChanged()
    {
        if (sync is null || sync.Status != SyncStatus.Idle || sync.LastSyncedAt == lastSyncSeen)
        {
            return;
        }

        // A sync finished: the server is reachable and photo details that were not on it may be now. Uploads that waited for either go now;
        // ones that met a server error keep their wait.
        lastSyncSeen = sync.LastSyncedAt;
        lock (gate)
        {
            foreach (var entry in entries.Values.Where(e => !e.Failed && e.LastProblem is UploadProblem.NotSynced or UploadProblem.Offline).ToList())
            {
                entries[entry.PhotoId] = entry with { NextAttemptAt = null };
            }
        }

        Kick();
    }

    private UploadItem ToItem(Entry entry) => new(
        entry.PhotoId,
        entry.Failed ? UploadStatus.Failed : entry.PhotoId == uploading ? UploadStatus.Uploading : UploadStatus.Waiting,
        entry.Attempts,
        entry.NextAttemptAt,
        entry.LastError);

    private Task PersistAsync(Entry entry, CancellationToken cancellationToken) =>
        store.PutAsync(Collection, entry.PhotoId, JsonSerializer.Serialize(entry, Json), cancellationToken);

    private static Entry? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Entry>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
