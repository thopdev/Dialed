using Dialed.Client.Data;
using Dialed.Client.Platform;
using Dialed.Client.Sync;
using Dialed.Domain.Model;

namespace Dialed.Client.Tests;

/// <summary>Only one tab syncs at a time (#39): the coordinator holds the cross-tab lock while syncing and waits when another tab has it.</summary>
public sealed class CrossTabSyncTests : IDisposable
{
    /// <summary>Stands in for the Web Locks API: "another tab" can hold the lock, and this tab's holds are counted.</summary>
    private sealed class FakeCrossTabLock : ICrossTabLock
    {
        private TaskCompletionSource otherTab = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool OtherTabHolds { get; private set; }

        public bool HeldHere { get; private set; }

        public int Acquired { get; private set; }

        public int Waits { get; private set; }

        public List<string> Names { get; } = [];

        /// <summary>When set, taking the lock waits for it, so a test can start a second sync meanwhile.</summary>
        public TaskCompletionSource? Pause { get; set; }

        public void OtherTabTakes()
        {
            otherTab = new(TaskCreationOptions.RunContinuationsAsynchronously);
            OtherTabHolds = true;
        }

        public void OtherTabReleases()
        {
            OtherTabHolds = false;
            otherTab.TrySetResult();
        }

        public async Task<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken cancellationToken = default)
        {
            Names.Add(name);
            if (Pause is { } pause)
            {
                await pause.Task.WaitAsync(cancellationToken);
            }

            return OtherTabHolds || HeldHere ? null : Take();
        }

        public async Task<IAsyncDisposable> AcquireAsync(string name, CancellationToken cancellationToken = default)
        {
            Names.Add(name);
            Waits++;
            if (OtherTabHolds)
            {
                await otherTab.Task.WaitAsync(cancellationToken);
            }

            return Take();
        }

        private Held Take()
        {
            HeldHere = true;
            Acquired++;
            return new Held(this);
        }

        private sealed class Held(FakeCrossTabLock owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                owner.HeldHere = false;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class Ctx : ICurrentContext
    {
        public string UserId => "u1";

        public string HouseholdId => "h1";
    }

    private sealed class Token : IAccessTokenProvider
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("tkn");
    }

    private sealed class Network : IConnectivity
    {
        public bool IsOnline => true;

        public event Action<bool>? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class Lifecycle : IAppLifecycle
    {
        public bool IsForeground => true;

        public event Action? Resumed
        {
            add { }
            remove { }
        }

        public event Action? Paused
        {
            add { }
            remove { }
        }
    }

    private readonly Clock time = new();
    private readonly FakeSyncServer server;
    private readonly InMemoryRepository<Vendor> vendors;
    private readonly InMemorySyncState state = new();
    private readonly FakeCrossTabLock tabs = new();
    private readonly SyncCoordinator coordinator;

    public CrossTabSyncTests()
    {
        server = new FakeSyncServer(time);
        vendors = new InMemoryRepository<Vendor>(new Ctx(), time);
        var engine = new SyncEngine([new SyncTable<Vendor>("vendors", vendors, state, server, time).Attach()], state);
        coordinator = new SyncCoordinator(engine, state, new Token(), new Network(), new Lifecycle(), time, debounce: TimeSpan.FromHours(1), tabLock: tabs);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => coordinator.Dispose();

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25, Ct);
        }

        condition().ShouldBeTrue();
    }

    [Fact]
    public async Task Holds_the_lock_while_syncing_and_frees_it_after()
    {
        var heldWhileSyncing = false;
        coordinator.Changed += () => heldWhileSyncing |= coordinator.Status == SyncStatus.Syncing && tabs.HeldHere;
        await vendors.SaveAsync(new Vendor { Name = "A" }, Ct);

        var result = await coordinator.SyncNowAsync(Ct);

        result.Outcome.ShouldBe(SyncOutcome.Completed);
        heldWhileSyncing.ShouldBeTrue();
        tabs.HeldHere.ShouldBeFalse();
        tabs.Acquired.ShouldBe(1);
        tabs.Names.ShouldAllBe(name => name == SyncCoordinator.LockName);
    }

    [Fact]
    public async Task Frees_the_lock_when_the_server_cannot_be_reached()
    {
        server.Offline = true;
        await vendors.SaveAsync(new Vendor { Name = "A" }, Ct);

        await coordinator.SyncNowAsync(Ct);

        coordinator.Status.ShouldBe(SyncStatus.Offline);
        tabs.HeldHere.ShouldBeFalse();
    }

    [Fact]
    public async Task While_another_tab_syncs_this_one_says_so_and_does_not_sync()
    {
        tabs.OtherTabTakes();
        await vendors.SaveAsync(new Vendor { Name = "A" }, Ct);

        var result = await coordinator.SyncNowAsync(Ct);

        result.Outcome.ShouldBe(SyncOutcome.AlreadyRunning);
        coordinator.Status.ShouldBe(SyncStatus.SyncingElsewhere);
        server.PushCalls.ShouldBe(0);
        server.PullCalls.ShouldBe(0);
        coordinator.PendingCount.ShouldBe(0, "nothing refreshed yet; the count comes after the sync");
    }

    [Fact]
    public async Task Syncs_here_once_the_other_tab_is_done()
    {
        tabs.OtherTabTakes();
        await vendors.SaveAsync(new Vendor { Name = "A" }, Ct);
        await coordinator.SyncNowAsync(Ct);

        tabs.OtherTabReleases();

        await Eventually(() => coordinator.LastResult is not null && coordinator.Status == SyncStatus.Idle);
        coordinator.LastResult!.Pushed.ShouldBe(1);
        server.All("vendors").Count().ShouldBe(1);
        tabs.HeldHere.ShouldBeFalse();
        tabs.Acquired.ShouldBe(1, "the sync uses the lock it waited for, instead of freeing it and racing to take it again");
    }

    [Fact]
    public async Task A_second_sync_in_the_same_tab_is_not_mistaken_for_another_tab()
    {
        tabs.Pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = coordinator.SyncNowAsync(Ct);

        (await coordinator.SyncNowAsync(Ct)).Outcome.ShouldBe(SyncOutcome.AlreadyRunning);
        coordinator.Status.ShouldNotBe(SyncStatus.SyncingElsewhere);

        tabs.Pause.SetResult();
        (await first).Outcome.ShouldBe(SyncOutcome.Completed);
        tabs.Waits.ShouldBe(0);
    }

    [Fact]
    public async Task More_requests_while_waiting_do_not_queue_more_syncs()
    {
        tabs.OtherTabTakes();
        await coordinator.SyncNowAsync(Ct);
        (await coordinator.SyncNowAsync(Ct)).Outcome.ShouldBe(SyncOutcome.AlreadyRunning);
        (await coordinator.SyncNowAsync(Ct)).Outcome.ShouldBe(SyncOutcome.AlreadyRunning);

        tabs.OtherTabReleases();

        await Eventually(() => coordinator.LastResult is not null);
        tabs.Waits.ShouldBe(1);
        server.PullCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Disposing_stops_waiting_for_the_other_tab()
    {
        tabs.OtherTabTakes();
        await coordinator.SyncNowAsync(Ct);

        coordinator.Dispose();
        tabs.OtherTabReleases();
        await Task.Delay(100, Ct);

        server.PullCalls.ShouldBe(0);
        coordinator.LastResult.ShouldBeNull();
    }
}
