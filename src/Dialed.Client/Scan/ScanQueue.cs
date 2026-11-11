using System.Text.Json;
using Dialed.Client.Data;
using Dialed.Client.Platform;
using Dialed.Client.Storage;
using Dialed.Domain.Model;

namespace Dialed.Client.Scan;

public enum PendingScanStatus
{
    /// <summary>The photo is saved; the label is read once the photo has reached the server and there is a network and a sign-in.</summary>
    Waiting,

    Reading,

    /// <summary>Read: the kept draft can be reviewed and saved.</summary>
    Ready,

    /// <summary>Could not be read (quota used up, AI switched off, or repeated server trouble). A manual retry starts it again.</summary>
    Failed,
}

/// <summary>One scan in the queue, as the UI sees it.</summary>
public sealed record PendingScan(string PhotoId, PendingScanStatus Status, ScannedLabel? Draft, ScanProblem? Problem, DateTimeOffset QueuedAt);

/// <summary>
/// Label scans taken without a connection (#83). The photo is saved like any other (and uploaded by the upload queue); once it has reached
/// the server the label is read by photo id and the draft is kept here until the person reviews it.
/// </summary>
public interface IScanQueue
{
    /// <summary>Everything in the queue, oldest first.</summary>
    IReadOnlyList<PendingScan> Items { get; }

    /// <summary>Scans still to be read.</summary>
    int WaitingCount { get; }

    /// <summary>Scans with a draft to review.</summary>
    int ReadyCount { get; }

    PendingScan? Get(string photoId);

    /// <summary>Adds a scan for a photo that is saved on this device and starts reading when possible.</summary>
    Task EnqueueAsync(string photoId, CancellationToken cancellationToken = default);

    /// <summary>Takes a scan out of the queue (reviewed and saved, or thrown away).</summary>
    Task RemoveAsync(string photoId, CancellationToken cancellationToken = default);

    /// <summary>Starts a failed scan again.</summary>
    Task RetryAsync(string photoId, CancellationToken cancellationToken = default);

    event Action? Changed;
}

/// <summary>
/// The scan queue. It reads one scan at a time, whenever something may have changed: a scan was added, the network came back, the app
/// returned to the foreground or a photo finished uploading. Being offline, signed out or waiting for the upload never counts as a failure;
/// server trouble does, three times in a row.
/// </summary>
public sealed class PendingScanQueue : IScanQueue, ILoadable, IDisposable
{
    public const string Collection = "scans";

    /// <summary>Server errors in a row before the queue gives up on a scan.</summary>
    public const int MaxFailures = 3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ILocalStore store;
    private readonly IRepository<Photo> photos;
    private readonly ILabelScanner scanner;
    private readonly IConnectivity connectivity;
    private readonly IAppLifecycle lifecycle;
    private readonly TimeProvider time;
    private readonly Lock gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private bool running;
    private bool runAgain;
    private bool started;
    private bool disposed;

    public PendingScanQueue(ILocalStore store, IRepository<Photo> photos, ILabelScanner scanner, IConnectivity connectivity, IAppLifecycle lifecycle, TimeProvider time)
    {
        this.store = store;
        this.photos = photos;
        this.scanner = scanner;
        this.connectivity = connectivity;
        this.lifecycle = lifecycle;
        this.time = time;
    }

    private sealed record Entry(
        string PhotoId,
        DateTimeOffset QueuedAt,
        PendingScanStatus Status = PendingScanStatus.Waiting,
        int Failures = 0,
        ScanProblem? Problem = null,
        ScannedLabel? Draft = null);

    public event Action? Changed;

    public IReadOnlyList<PendingScan> Items
    {
        get
        {
            lock (gate)
            {
                return [.. entries.Values.OrderBy(e => e.QueuedAt).ThenBy(e => e.PhotoId, StringComparer.Ordinal).Select(ToItem)];
            }
        }
    }

    public int WaitingCount
    {
        get
        {
            lock (gate)
            {
                return entries.Values.Count(e => e.Status is PendingScanStatus.Waiting or PendingScanStatus.Reading);
            }
        }
    }

    public int ReadyCount
    {
        get
        {
            lock (gate)
            {
                return entries.Values.Count(e => e.Status == PendingScanStatus.Ready);
            }
        }
    }

    public PendingScan? Get(string photoId)
    {
        lock (gate)
        {
            return entries.TryGetValue(photoId, out var entry) ? ToItem(entry) : null;
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
                    // A scan that was being read when the app closed is simply waiting again.
                    entries[entry.PhotoId] = entry.Status == PendingScanStatus.Reading ? entry with { Status = PendingScanStatus.Waiting } : entry;
                }
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Starts listening for a network, the app coming back and photos finishing their upload, and reads what is due. Call once after <see cref="LoadAsync"/>.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (started || disposed)
            {
                return Task.CompletedTask;
            }

            started = true;
        }

        connectivity.Changed += OnConnectivityChanged;
        lifecycle.Resumed += Kick;
        photos.Changed += Kick;
        Kick();
        return Task.CompletedTask;
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

    public async Task RetryAsync(string photoId, CancellationToken cancellationToken = default)
    {
        Entry? reset = null;
        lock (gate)
        {
            if (entries.TryGetValue(photoId, out var entry) && entry.Status == PendingScanStatus.Failed)
            {
                reset = entry with { Status = PendingScanStatus.Waiting, Failures = 0, Problem = null };
                entries[photoId] = reset;
            }
        }

        if (reset is not null)
        {
            await PersistAsync(reset, cancellationToken);
            Changed?.Invoke();
            Kick();
        }
    }

    /// <summary>Reads every scan that is due now, one after the other. Safe to call at any time; a call while running makes it look again.</summary>
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

                foreach (var photoId in WaitingIds())
                {
                    if (!connectivity.IsOnline || !await ReadOneAsync(photoId, cancellationToken))
                    {
                        break;
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
            }
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
        }

        connectivity.Changed -= OnConnectivityChanged;
        lifecycle.Resumed -= Kick;
        photos.Changed -= Kick;
    }

    private List<string> WaitingIds()
    {
        lock (gate)
        {
            return [.. entries.Values.Where(e => e.Status == PendingScanStatus.Waiting).OrderBy(e => e.QueuedAt).Select(e => e.PhotoId)];
        }
    }

    private bool RunAgain()
    {
        lock (gate)
        {
            return runAgain && !disposed;
        }
    }

    /// <summary>Returns <c>false</c> when nothing else would work right now (signed out), so the loop stops.</summary>
    private async Task<bool> ReadOneAsync(string photoId, CancellationToken cancellationToken)
    {
        var photo = await photos.GetAsync(photoId, cancellationToken);
        if (photo is null || photo.Deleted)
        {
            await RemoveAsync(photoId, cancellationToken);
            return true;
        }

        // Only a photo that has reached the server can be read there.
        if (photo.UploadState != UploadState.Uploaded)
        {
            return true;
        }

        await UpdateAsync(photoId, e => e with { Status = PendingScanStatus.Reading }, cancellationToken);
        try
        {
            var label = await scanner.ScanStoredAsync(photoId, cancellationToken);
            await UpdateAsync(photoId, e => e with { Status = PendingScanStatus.Ready, Draft = label, Problem = null, Failures = 0 }, cancellationToken);
            return true;
        }
        catch (LabelScanException exception)
        {
            switch (exception.Problem)
            {
                case ScanProblem.SignedOut:
                    await UpdateAsync(photoId, e => e with { Status = PendingScanStatus.Waiting }, cancellationToken);
                    return false;
                case ScanProblem.Offline or ScanProblem.NotUploaded:
                    await UpdateAsync(photoId, e => e with { Status = PendingScanStatus.Waiting }, cancellationToken);
                    return exception.Problem != ScanProblem.Offline;
                case ScanProblem.QuotaUsed or ScanProblem.Disabled:
                    await UpdateAsync(photoId, e => e with { Status = PendingScanStatus.Failed, Problem = exception.Problem }, cancellationToken);
                    return true;
                default:
                    await UpdateAsync(
                        photoId,
                        e => e.Failures + 1 >= MaxFailures
                            ? e with { Status = PendingScanStatus.Failed, Failures = e.Failures + 1, Problem = exception.Problem }
                            : e with { Status = PendingScanStatus.Waiting, Failures = e.Failures + 1 },
                        cancellationToken);
                    return true;
            }
        }
    }

    private async Task UpdateAsync(string photoId, Func<Entry, Entry> change, CancellationToken cancellationToken)
    {
        Entry? next = null;
        lock (gate)
        {
            if (entries.TryGetValue(photoId, out var entry))
            {
                next = change(entry);
                entries[photoId] = next;
            }
        }

        if (next is not null)
        {
            await PersistAsync(next, cancellationToken);
            Changed?.Invoke();
        }
    }

    private void OnConnectivityChanged(bool online)
    {
        if (online)
        {
            Kick();
        }
    }

    private void Kick() => _ = ProcessAsync();

    private Task PersistAsync(Entry entry, CancellationToken cancellationToken) =>
        store.PutAsync(Collection, entry.PhotoId, JsonSerializer.Serialize(entry, Json), cancellationToken);

    private static PendingScan ToItem(Entry entry) => new(entry.PhotoId, entry.Status, entry.Draft, entry.Problem, entry.QueuedAt);

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
