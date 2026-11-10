using Dialed.Client.Data;
using Dialed.Client.Platform;
using Dialed.Client.Storage;
using Dialed.Client.Sync;
using Dialed.Domain.Model;

namespace Dialed.Client.Tests;

/// <summary>Storage trouble and recovery (#114): a full disk, loading everything again after the browser cleared it, and changes stuck unsent.</summary>
public sealed class StorageRecoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- A full or blocked disk -------------------------------------------------------------------

    [Fact]
    public async Task A_failed_write_is_reported_not_thrown_and_the_next_good_write_clears_it()
    {
        var inner = new FlakyStore { Failing = true };
        var status = new StorageStatus();
        var store = new ResilientLocalStore(inner, status);

        await store.PutAsync("beans", "1", "{}", Ct);

        status.WriteFailed.ShouldBeTrue();
        inner.Failing = false;
        await store.PutAsync("beans", "2", "{}", Ct);
        status.WriteFailed.ShouldBeFalse();
        inner.Written.ShouldBe(["2"]);
    }

    [Fact]
    public async Task Every_kind_of_write_is_guarded()
    {
        var status = new StorageStatus();
        var store = new ResilientLocalStore(new FlakyStore { Failing = true }, status);

        await store.RemoveAsync("beans", "1", Ct);
        status.WriteFailed.ShouldBeTrue();
        status.ReportWriteSucceeded();

        await store.ClearCollectionAsync("beans", Ct);
        status.WriteFailed.ShouldBeTrue();
        status.ReportWriteSucceeded();

        await store.ClearAsync(Ct);
        status.WriteFailed.ShouldBeTrue();
    }

    [Fact]
    public async Task Reading_is_not_hidden_and_a_cancelled_write_still_cancels()
    {
        var store = new ResilientLocalStore(new FlakyStore { Failing = true }, new StorageStatus());

        await Should.ThrowAsync<IOException>(() => store.ReadAllAsync("beans", Ct));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Should.ThrowAsync<IOException>(() => store.PutAsync("beans", "1", "{}", cancelled.Token));
    }

    [Fact]
    public async Task Saving_a_record_still_works_when_the_disk_is_full_and_the_record_is_kept_in_memory_and_queued_to_sync()
    {
        var status = new StorageStatus();
        var store = new ResilientLocalStore(new FlakyStore { Failing = true }, status);
        var beans = new InMemoryRepository<Bean>(new Ctx(), TimeProvider.System, store);
        var state = new InMemorySyncState(store);
        new SyncTable<Bean>("beans", beans, state, new FakeSyncServer(TimeProvider.System), TimeProvider.System).Attach();

        var saved = await beans.SaveAsync(new Bean { Name = "Gesha" }, Ct);

        (await beans.GetAsync(saved.Id, Ct))!.Name.ShouldBe("Gesha");
        status.WriteFailed.ShouldBeTrue();
        (await state.PendingAsync(Ct)).Count.ShouldBe(1);
    }

    // ---- Loading everything again (new device, or the browser cleared the data) --------------------

    private sealed class Engine
    {
        public Engine(int vendorsOnServer, int pageSize = 2)
        {
            Server = new FakeSyncServer(TimeProvider.System);
            for (var i = 0; i < vendorsOnServer; i++)
            {
                Server.Seed("vendors", new Vendor { Id = $"v{i:000}", Name = $"V{i}", HouseholdId = "h1", UpdatedAt = DateTimeOffset.UnixEpoch.AddMinutes(i) });
            }

            Vendors = new InMemoryRepository<Vendor>(new Ctx(), TimeProvider.System);
            State = new InMemorySyncState();
            Sync = new SyncEngine([new SyncTable<Vendor>("vendors", Vendors, State, Server, TimeProvider.System).Attach()], State, pageSize);
        }

        public FakeSyncServer Server { get; }

        public InMemoryRepository<Vendor> Vendors { get; }

        public InMemorySyncState State { get; }

        public SyncEngine Sync { get; }
    }

    [Fact]
    public async Task A_first_pull_reports_progress_page_by_page_with_a_running_total()
    {
        var engine = new Engine(vendorsOnServer: 5);
        List<PullProgress> seen = [];
        engine.Sync.PullProgressed += seen.Add;

        await engine.Sync.SyncAsync(Ct);

        seen.ShouldAllBe(p => p.FirstPull);
        seen.Select(p => p.Applied).ShouldBe([2, 4, 5]);
        (await engine.Vendors.ListAsync(Ct)).Count.ShouldBe(5);
    }

    [Fact]
    public async Task A_later_pull_is_not_a_first_pull()
    {
        var engine = new Engine(vendorsOnServer: 3);
        await engine.Sync.SyncAsync(Ct);
        engine.Server.Seed("vendors", new Vendor { Id = "v999", Name = "New", HouseholdId = "h1", UpdatedAt = DateTimeOffset.UnixEpoch.AddDays(1) });
        List<PullProgress> seen = [];
        engine.Sync.PullProgressed += seen.Add;

        await engine.Sync.SyncAsync(Ct);

        seen.ShouldNotBeEmpty();
        seen.ShouldAllBe(p => !p.FirstPull);
    }

    [Fact]
    public async Task After_the_local_data_is_cleared_everything_comes_back_from_the_server()
    {
        var engine = new Engine(vendorsOnServer: 4);
        await engine.Sync.SyncAsync(Ct);
        (await engine.Vendors.ListAsync(Ct)).Count.ShouldBe(4);

        // The browser wiped the site's storage: a fresh database and fresh sync state, the server unchanged.
        var restored = new Engine(vendorsOnServer: 0) { };
        foreach (var vendor in engine.Server.All("vendors").Cast<Vendor>())
        {
            restored.Server.Seed("vendors", vendor);
        }

        List<PullProgress> seen = [];
        restored.Sync.PullProgressed += seen.Add;
        await restored.Sync.SyncAsync(Ct);

        (await restored.Vendors.ListAsync(Ct)).Select(v => v.Name).Order().ShouldBe(["V0", "V1", "V2", "V3"]);
        seen.ShouldAllBe(p => p.FirstPull);
    }

    [Fact]
    public async Task The_coordinator_shows_the_running_count_while_loading_and_clears_it_afterwards()
    {
        var engine = new Engine(vendorsOnServer: 5);
        var coordinator = Coordinator(engine, new Clock());
        List<int> counts = [];
        coordinator.Changed += () => counts.Add(coordinator.RestoringCount);

        await coordinator.SyncNowAsync(Ct);

        counts.ShouldContain(2);
        counts.ShouldContain(4);
        counts.ShouldContain(5);
        coordinator.RestoringCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_brand_new_account_with_nothing_to_load_shows_no_loading_message()
    {
        var engine = new Engine(vendorsOnServer: 0);
        var coordinator = Coordinator(engine, new Clock());
        var shown = false;
        coordinator.Changed += () => shown |= coordinator.RestoringCount > 0;

        await coordinator.SyncNowAsync(Ct);

        shown.ShouldBeFalse();
    }

    // ---- Changes that stay unsent ---------------------------------------------------------------

    [Fact]
    public async Task Changes_that_wait_longer_than_five_minutes_are_flagged_as_stuck()
    {
        var clock = new Clock();
        var engine = new Engine(vendorsOnServer: 0);
        engine.Server.Offline = true;
        var coordinator = Coordinator(engine, clock);
        await engine.Vendors.SaveAsync(new Vendor { Name = "Waiting" }, Ct);
        await coordinator.SyncNowAsync(Ct);

        coordinator.PendingCount.ShouldBe(1);
        coordinator.HasStuckChanges.ShouldBeFalse();
        clock.Now += TimeSpan.FromMinutes(4);
        coordinator.HasStuckChanges.ShouldBeFalse();
        clock.Now += TimeSpan.FromMinutes(1);
        coordinator.HasStuckChanges.ShouldBeTrue();
    }

    [Fact]
    public async Task Once_the_changes_are_sent_the_warning_goes_and_the_clock_restarts_for_new_ones()
    {
        var clock = new Clock();
        var engine = new Engine(vendorsOnServer: 0);
        engine.Server.Offline = true;
        var coordinator = Coordinator(engine, clock);
        await engine.Vendors.SaveAsync(new Vendor { Name = "First" }, Ct);
        await coordinator.SyncNowAsync(Ct);
        clock.Now += TimeSpan.FromMinutes(10);
        coordinator.HasStuckChanges.ShouldBeTrue();

        engine.Server.Offline = false;
        await coordinator.SyncNowAsync(Ct);

        coordinator.PendingCount.ShouldBe(0);
        coordinator.PendingSince.ShouldBeNull();
        coordinator.HasStuckChanges.ShouldBeFalse();

        engine.Server.Offline = true;
        await engine.Vendors.SaveAsync(new Vendor { Name = "Second" }, Ct);
        await coordinator.SyncNowAsync(Ct);
        coordinator.PendingSince.ShouldBe(clock.Now);
        coordinator.HasStuckChanges.ShouldBeFalse();
    }

    [Fact]
    public async Task Without_an_account_waiting_changes_are_never_called_stuck()
    {
        var clock = new Clock();
        var engine = new Engine(vendorsOnServer: 0);
        var coordinator = Coordinator(engine, clock, token: null);
        await engine.Vendors.SaveAsync(new Vendor { Name = "Local only" }, Ct);
        await coordinator.SyncNowAsync(Ct);
        clock.Now += TimeSpan.FromHours(1);

        coordinator.PendingCount.ShouldBe(1);
        coordinator.HasStuckChanges.ShouldBeFalse();
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static SyncCoordinator Coordinator(Engine engine, Clock clock, string? token = "tkn") =>
        new(engine.Sync, engine.State, new Token(token), new Network(), new Lifecycle(), clock, debounce: TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Ctx : ICurrentContext
    {
        public string UserId => "u1";

        public string HouseholdId => "h1";
    }

    private sealed class Token(string? value) : IAccessTokenProvider
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(value);
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

    private sealed class FlakyStore : ILocalStore
    {
        public bool Failing { get; set; }

        public List<string> Written { get; } = [];

        public Task<IReadOnlyDictionary<string, string>> ReadAllAsync(string collection, CancellationToken cancellationToken = default) =>
            Failing ? throw new IOException("Storage unavailable.") : Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task PutAsync(string collection, string key, string json, CancellationToken cancellationToken = default)
        {
            if (Failing)
            {
                throw new IOException("QuotaExceededError");
            }

            Written.Add(key);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string collection, string key, CancellationToken cancellationToken = default) =>
            Failing ? throw new IOException("QuotaExceededError") : Task.CompletedTask;

        public Task ClearCollectionAsync(string collection, CancellationToken cancellationToken = default) =>
            Failing ? throw new IOException("QuotaExceededError") : Task.CompletedTask;

        public Task ClearAsync(CancellationToken cancellationToken = default) =>
            Failing ? throw new IOException("QuotaExceededError") : Task.CompletedTask;
    }
}
