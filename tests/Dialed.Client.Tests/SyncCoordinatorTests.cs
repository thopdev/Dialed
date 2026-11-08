using Dialed.Client.Data;
using Dialed.Client.Platform;
using Dialed.Client.Sync;
using Dialed.Domain.Model;

namespace Dialed.Client.Tests;

public sealed class SyncCoordinatorTests : IDisposable
{
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class Ctx : ICurrentContext
    {
        public string UserId => "u1";

        public string HouseholdId => "h1";
    }

    private sealed class Token(string? value) : IAccessTokenProvider
    {
        public string? Value { get; set; } = value;

        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
    }

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

    private readonly Clock time = new();
    private readonly FakeSyncServer server;
    private readonly InMemoryRepository<Vendor> vendors;
    private readonly InMemorySyncState state = new();
    private readonly Token token = new("tkn");
    private readonly Network network = new();
    private readonly Lifecycle lifecycle = new();
    private readonly SyncCoordinator coordinator;

    public SyncCoordinatorTests()
    {
        server = new FakeSyncServer(time);
        vendors = new InMemoryRepository<Vendor>(new Ctx(), time);
        var engine = new SyncEngine([new SyncTable<Vendor>("vendors", vendors, state, server, time).Attach()], state);
        coordinator = new SyncCoordinator(engine, state, token, network, lifecycle, time, debounce: TimeSpan.Zero);
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
    public async Task Starting_syncs_once_and_reports_the_result()
    {
        await vendors.SaveAsync(new Vendor { Name = "A" }, Ct);

        await coordinator.StartAsync();

        coordinator.Status.ShouldBe(SyncStatus.Idle);
        coordinator.LastResult!.Pushed.ShouldBe(1);
        coordinator.LastSyncedAt.ShouldNotBeNull();
        coordinator.PendingCount.ShouldBe(0);
        server.All("vendors").Count().ShouldBe(1);
    }

    [Fact]
    public async Task Not_signed_in_keeps_changes_local_and_says_so()
    {
        token.Value = null;
        await vendors.SaveAsync(new Vendor { Name = "A" }, Ct);

        await coordinator.StartAsync();

        coordinator.Status.ShouldBe(SyncStatus.NeedsSignIn);
        coordinator.PendingCount.ShouldBe(1);
        server.PushCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Offline_leaves_changes_pending_and_the_next_sync_recovers()
    {
        server.Offline = true;
        await vendors.SaveAsync(new Vendor { Name = "A" }, Ct);

        await coordinator.StartAsync();
        coordinator.Status.ShouldBe(SyncStatus.Offline);
        coordinator.PendingCount.ShouldBe(1);

        server.Offline = false;
        await coordinator.SyncNowAsync(Ct);
        coordinator.Status.ShouldBe(SyncStatus.Idle);
        coordinator.PendingCount.ShouldBe(0);
    }

    [Fact]
    public async Task No_network_does_not_even_try()
    {
        network.IsOnline = false;
        await vendors.SaveAsync(new Vendor { Name = "A" }, Ct);

        await coordinator.StartAsync();

        coordinator.Status.ShouldBe(SyncStatus.Offline);
        server.PushCalls.ShouldBe(0);
    }

    [Fact]
    public async Task A_local_change_triggers_a_sync()
    {
        await coordinator.StartAsync();

        await vendors.SaveAsync(new Vendor { Name = "B" }, Ct);

        await Eventually(() => server.All("vendors").Any());
        await Eventually(() => coordinator.PendingCount == 0);
    }

    [Fact]
    public async Task Regaining_the_network_triggers_a_sync()
    {
        network.IsOnline = false;
        await vendors.SaveAsync(new Vendor { Name = "A" }, Ct);
        await coordinator.StartAsync();
        server.All("vendors").ShouldBeEmpty();

        network.Set(true);

        await Eventually(() => server.All("vendors").Any());
    }

    [Fact]
    public async Task Losing_the_network_shows_offline()
    {
        await coordinator.StartAsync();
        network.Set(false);
        coordinator.Status.ShouldBe(SyncStatus.Offline);
    }

    [Fact]
    public async Task Returning_to_the_foreground_pulls_changes_from_other_devices()
    {
        await coordinator.StartAsync();
        server.Seed("vendors", new Vendor { Id = "from-elsewhere", Name = "Other device", HouseholdId = "h1" });

        lifecycle.Resume();

        await Eventually(() => vendors.ListAsync(Ct).Result.Any(v => v.Id == "from-elsewhere"));
    }

    [Fact]
    public async Task Several_quick_changes_become_one_sync_when_debounced()
    {
        using var debounced = new SyncCoordinator(
            new SyncEngine([new SyncTable<Vendor>("vendors", vendors, state, server, time)], state),
            state, token, network, lifecycle, TimeProvider.System, debounce: TimeSpan.FromMilliseconds(200));

        for (var i = 0; i < 5; i++)
        {
            debounced.RequestSync();
        }

        await Task.Delay(100, Ct);
        server.PullCalls.ShouldBe(0);
        await Eventually(() => server.PullCalls == 1);
        await Task.Delay(400, Ct);
        server.PullCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Disposed_coordinators_ignore_triggers()
    {
        await coordinator.StartAsync();
        var calls = server.PullCalls;
        coordinator.Dispose();

        network.Set(true);
        lifecycle.Resume();
        await Task.Delay(150, Ct);

        server.PullCalls.ShouldBe(calls);
    }
}
