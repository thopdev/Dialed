using Dialed.Client.Data;
using Dialed.Client.Platform;
using Dialed.Client.Scan;
using Dialed.Client.Storage;
using Dialed.Domain.Model;

namespace Dialed.Client.Tests;

/// <summary>Label scans taken offline wait for their photo to upload, are then read by photo id and kept for review (#83).</summary>
public sealed class ScanQueueTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Network : IConnectivity
    {
        public bool IsOnline { get; set; } = true;

        public event Action<bool>? Changed;

        public void Set(bool online)
        {
            IsOnline = online;
            Changed?.Invoke(online);
        }
    }

    private sealed class Lifecycle : IAppLifecycle
    {
        public bool IsForeground => true;

        public event Action? Resumed;

        public event Action? Paused;

        public void Resume() => Resumed?.Invoke();

        public void Pause() => Paused?.Invoke();
    }

    /// <summary>Answers <see cref="ILabelScanner.ScanStoredAsync"/> with the next scripted outcome; <c>null</c> means a good label.</summary>
    private sealed class FakeScanner : ILabelScanner
    {
        public Queue<ScanProblem?> Script { get; } = new();

        /// <summary>While set, every call fails as signed out, like the real scanner without a session.</summary>
        public bool SignedOut { get; set; }

        public List<string> Asked { get; } = [];

        public Task<ScannedLabel> ScanAsync(CapturedPhoto photo, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ScannedLabel> ScanStoredAsync(string photoId, CancellationToken cancellationToken = default)
        {
            lock (Asked)
            {
                Asked.Add(photoId);
            }

            if (SignedOut)
            {
                throw new LabelScanException(ScanProblem.SignedOut);
            }

            return Script.TryDequeue(out var problem) && problem is { } p
                ? throw new LabelScanException(p)
                : Task.FromResult(Label("Guji"));
        }
    }

    private static ScannedLabel Label(string name) => new(
        new ScannedField(name, 0.9), new ScannedField("Friedhats", 0.9), new ScannedField("Ethiopia", 0.4), new ScannedField(null, 0), new ScannedField(null, 0),
        new ScannedField("Natural", 0.9), new ScannedField(null, 0), new ScannedField(null, 0), new ScannedField(null, 0), new ScannedField(null, 0),
        new ScannedField("250", 0.9), "label-v1", "fake");

    /// <summary>A clock whose timers only fire when the test says so.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly List<(TimerCallback Callback, object? State, TimeSpan Due)> timers = [];

        public IReadOnlyList<TimeSpan> Delays
        {
            get
            {
                lock (timers)
                {
                    return [.. timers.Select(t => t.Due)];
                }
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (timers)
            {
                timers.Add((callback, state, dueTime));
            }

            return new NoTimer();
        }

        /// <summary>Runs the timers that were created, as if their time had come.</summary>
        public void FireAll()
        {
            List<(TimerCallback Callback, object? State, TimeSpan Due)> due;
            lock (timers)
            {
                due = [.. timers];
                timers.Clear();
            }

            foreach (var (callback, state, _) in due)
            {
                callback(state);
            }
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private readonly Network network = new();
    private readonly Lifecycle lifecycle = new();
    private readonly InMemoryLocalStore store = new();
    private readonly FakeScanner scanner = new();
    private readonly InMemoryRepository<Photo> photos;
    private readonly List<PendingScanQueue> queues = [];

    public ScanQueueTests()
    {
        photos = new InMemoryRepository<Photo>(new LocalContext(), TimeProvider.System, store);
    }

    public void Dispose()
    {
        foreach (var queue in queues)
        {
            queue.Dispose();
        }
    }

    private async Task<PendingScanQueue> Started(InMemoryLocalStore? use = null)
    {
        var queue = new PendingScanQueue(use ?? store, photos, scanner, network, lifecycle, TimeProvider.System);
        queues.Add(queue);
        await queue.LoadAsync(Ct);
        await queue.StartAsync(Ct);
        return queue;
    }

    private async Task<Photo> SavedPhoto(UploadState state = UploadState.Pending) =>
        await photos.SaveAsync(new Photo { ContentType = "image/jpeg", SizeBytes = 10, UploadState = state }, Ct);

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(until, what);
            await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public async Task A_scan_waits_for_its_photo_to_upload_and_is_then_read_and_kept()
    {
        var queue = await Started();
        var photo = await SavedPhoto();

        await queue.EnqueueAsync(photo.Id, Ct);
        await queue.ProcessAsync(Ct);

        queue.Get(photo.Id)!.Status.ShouldBe(PendingScanStatus.Waiting);
        queue.WaitingCount.ShouldBe(1);
        scanner.Asked.ShouldBeEmpty();

        await photos.SaveAsync(photo with { UploadState = UploadState.Uploaded }, Ct);
        await Eventually(() => queue.ReadyCount == 1, "the scan was not read");

        var item = queue.Get(photo.Id)!;
        item.Status.ShouldBe(PendingScanStatus.Ready);
        item.Draft!.Name.Value.ShouldBe("Guji");
        queue.WaitingCount.ShouldBe(0);
    }

    [Fact]
    public async Task Offline_the_scan_stays_waiting_and_is_read_when_the_network_is_back()
    {
        var queue = await Started();
        var photo = await SavedPhoto(UploadState.Uploaded);
        network.IsOnline = false;

        await queue.EnqueueAsync(photo.Id, Ct);
        await queue.ProcessAsync(Ct);
        queue.Get(photo.Id)!.Status.ShouldBe(PendingScanStatus.Waiting);
        scanner.Asked.ShouldBeEmpty();

        network.Set(true);
        await Eventually(() => queue.ReadyCount == 1, "the scan was not read after going online");
    }

    [Fact]
    public async Task A_photo_the_server_does_not_have_yet_is_not_a_failure()
    {
        scanner.Script.Enqueue(ScanProblem.NotUploaded);
        var queue = await Started();
        var photo = await SavedPhoto(UploadState.Uploaded);

        await queue.EnqueueAsync(photo.Id, Ct);
        await Eventually(() => scanner.Asked.Count == 1, "the server was not asked");
        await Task.Delay(100, Ct);
        queue.Get(photo.Id)!.Status.ShouldBe(PendingScanStatus.Waiting);

        lifecycle.Resume();
        await Eventually(() => queue.ReadyCount == 1, "the scan was not read the second time");
    }

    [Fact]
    public async Task Signed_out_the_scans_wait_without_failing_and_are_read_after_signing_in()
    {
        scanner.SignedOut = true;
        var queue = await Started();
        var first = await SavedPhoto(UploadState.Uploaded);
        var second = await SavedPhoto(UploadState.Uploaded);

        await queue.EnqueueAsync(first.Id, Ct);
        await queue.EnqueueAsync(second.Id, Ct);
        await Eventually(() => scanner.Asked.Count >= 1, "nothing was asked");
        await Task.Delay(150, Ct);

        queue.Items.ShouldAllBe(i => i.Status == PendingScanStatus.Waiting);
        scanner.Asked.ShouldAllBe(id => id == first.Id); // the run stops at the first one: the rest would fail the same way

        scanner.SignedOut = false;
        lifecycle.Resume();
        await Eventually(() => queue.ReadyCount == 2, "the scans were not read after signing in");
    }

    [Fact]
    public async Task A_used_up_quota_fails_the_scan_with_its_reason_and_a_retry_starts_it_again()
    {
        scanner.Script.Enqueue(ScanProblem.QuotaUsed);
        var queue = await Started();
        var photo = await SavedPhoto(UploadState.Uploaded);

        await queue.EnqueueAsync(photo.Id, Ct);
        await Eventually(() => queue.Get(photo.Id)!.Status == PendingScanStatus.Failed, "the scan did not fail");
        queue.Get(photo.Id)!.Problem.ShouldBe(ScanProblem.QuotaUsed);

        await queue.RetryAsync(photo.Id, Ct);
        await Eventually(() => queue.ReadyCount == 1, "the retry was not read");
        queue.Get(photo.Id)!.Problem.ShouldBeNull();
    }

    [Fact]
    public async Task Repeated_server_trouble_fails_the_scan_after_three_tries()
    {
        for (var i = 0; i < PendingScanQueue.MaxFailures; i++)
        {
            scanner.Script.Enqueue(ScanProblem.Unavailable);
        }

        var queue = await Started();
        var photo = await SavedPhoto(UploadState.Uploaded);
        await queue.EnqueueAsync(photo.Id, Ct);

        for (var i = 0; i < PendingScanQueue.MaxFailures + 1 && queue.Get(photo.Id)!.Status != PendingScanStatus.Failed; i++)
        {
            await queue.ProcessAsync(Ct);
            await Task.Delay(50, Ct);
        }

        queue.Get(photo.Id)!.Status.ShouldBe(PendingScanStatus.Failed);
        queue.Get(photo.Id)!.Problem.ShouldBe(ScanProblem.Unavailable);
        scanner.Asked.Count.ShouldBe(PendingScanQueue.MaxFailures);
    }

    [Fact]
    public async Task Scans_and_their_drafts_survive_a_restart()
    {
        var queue = await Started();
        var ready = await SavedPhoto(UploadState.Uploaded);
        await queue.EnqueueAsync(ready.Id, Ct);
        await Eventually(() => queue.ReadyCount == 1, "the first scan was not read");
        var waiting = await SavedPhoto();
        await queue.EnqueueAsync(waiting.Id, Ct);

        var restarted = new PendingScanQueue(store, photos, scanner, network, lifecycle, TimeProvider.System);
        queues.Add(restarted);
        await restarted.LoadAsync(Ct);

        restarted.Items.Select(i => i.PhotoId).ShouldBe([ready.Id, waiting.Id]);
        restarted.Get(ready.Id)!.Draft!.Name.Value.ShouldBe("Guji");
        restarted.Get(ready.Id)!.Draft!.Country.IsUncertain.ShouldBeTrue();
        restarted.Get(waiting.Id)!.Status.ShouldBe(PendingScanStatus.Waiting);
    }

    [Fact]
    public async Task A_scan_whose_photo_was_deleted_drops_out_and_a_scan_can_be_removed()
    {
        var queue = await Started();
        var gone = await SavedPhoto();
        var kept = await SavedPhoto();
        await queue.EnqueueAsync(kept.Id, Ct);
        await queue.EnqueueAsync(gone.Id, Ct);

        await photos.DeleteAsync(gone.Id, Ct);
        await queue.ProcessAsync(Ct);

        await Eventually(() => queue.Get(gone.Id) is null, "the scan of a deleted photo stayed");
        queue.Get(kept.Id).ShouldNotBeNull();

        await queue.RemoveAsync(kept.Id, Ct);
        queue.Items.ShouldBeEmpty();
        (await store.ReadAllAsync(PendingScanQueue.Collection, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Changed_is_raised_when_a_scan_is_added_read_or_removed()
    {
        var queue = await Started();
        var changes = 0;
        queue.Changed += () => Interlocked.Increment(ref changes);
        var photo = await SavedPhoto(UploadState.Uploaded);

        await queue.EnqueueAsync(photo.Id, Ct);
        await Eventually(() => queue.ReadyCount == 1, "not read");
        var afterRead = changes;
        await queue.RemoveAsync(photo.Id, Ct);

        afterRead.ShouldBeGreaterThanOrEqualTo(3); // added, reading, ready
        changes.ShouldBe(afterRead + 1);
    }

    [Fact]
    public async Task After_a_server_error_the_scan_is_read_again_after_a_short_wait_without_any_other_trigger()
    {
        scanner.Script.Enqueue(ScanProblem.Unavailable);
        var clock = new ManualClock();
        var queue = new PendingScanQueue(store, photos, scanner, network, lifecycle, clock);
        queues.Add(queue);
        await queue.LoadAsync(Ct);
        var photo = await SavedPhoto(UploadState.Uploaded);

        await queue.EnqueueAsync(photo.Id, Ct);
        await Eventually(() => clock.Delays.Count == 1, "no retry was scheduled");

        clock.Delays.ShouldBe([PendingScanQueue.RetryStep]);
        queue.Get(photo.Id)!.Status.ShouldBe(PendingScanStatus.Waiting);

        clock.FireAll();
        await Eventually(() => queue.ReadyCount == 1, "the scan was not read after the wait");
    }

    [Fact]
    public async Task The_wait_grows_with_each_error_and_the_third_error_gives_up_without_scheduling_another()
    {
        for (var i = 0; i < PendingScanQueue.MaxFailures; i++)
        {
            scanner.Script.Enqueue(ScanProblem.Unavailable);
        }

        var clock = new ManualClock();
        var queue = new PendingScanQueue(store, photos, scanner, network, lifecycle, clock);
        queues.Add(queue);
        await queue.LoadAsync(Ct);
        var photo = await SavedPhoto(UploadState.Uploaded);

        await queue.EnqueueAsync(photo.Id, Ct);
        await Eventually(() => clock.Delays.Count == 1, "no first retry");
        clock.Delays[0].ShouldBe(PendingScanQueue.RetryStep);
        clock.FireAll();
        await Eventually(() => clock.Delays.Count == 1, "no second retry");
        clock.Delays[0].ShouldBe(PendingScanQueue.RetryStep * 2);
        clock.FireAll();

        await Eventually(() => queue.Get(photo.Id)!.Status == PendingScanStatus.Failed, "the scan did not give up");
        clock.Delays.ShouldBeEmpty();
    }
}
