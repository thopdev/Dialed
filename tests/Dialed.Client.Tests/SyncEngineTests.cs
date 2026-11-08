using Dialed.Client.Data;
using Dialed.Client.Sync;
using Dialed.Domain.Model;

namespace Dialed.Client.Tests;

public sealed class SyncEngineTests
{
    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class Context(string user) : ICurrentContext
    {
        public string UserId => user;

        public string HouseholdId => "h1";
    }

    /// <summary>One device: a local store for vendors and brews, its own outbox and engine.</summary>
    private sealed class Device
    {
        public Device(string user, FakeSyncServer server, FakeTime time, int pageSize = SyncEngine.DefaultPageSize)
        {
            Vendors = new InMemoryRepository<Vendor>(new Context(user), time);
            Brews = new InMemoryRepository<Brew>(new Context(user), time);
            State = new InMemorySyncState();
            Engine = new SyncEngine(
                [
                    new SyncTable<Vendor>("vendors", Vendors, State, server, time).Attach(),
                    new SyncTable<Brew>("brews", Brews, State, server, time).Attach(),
                ],
                State,
                pageSize);
        }

        public InMemoryRepository<Vendor> Vendors { get; }

        public InMemoryRepository<Brew> Brews { get; }

        public InMemorySyncState State { get; }

        public SyncEngine Engine { get; }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTime time = new(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeSyncServer server;

    public SyncEngineTests() => server = new FakeSyncServer(time);

    private Device NewDevice(string user = "u1", int pageSize = SyncEngine.DefaultPageSize) => new(user, server, time, pageSize);

    private Task<Vendor> AddVendor(Device device, string name) => device.Vendors.SaveAsync(new Vendor { Name = name }, Ct);

    [Fact]
    public async Task A_local_save_is_queued_and_pushed()
    {
        var device = NewDevice();
        var vendor = await AddVendor(device, "Tim Wendelboe");
        (await device.Engine.PendingCountAsync(Ct)).ShouldBe(1);

        var result = await device.Engine.SyncAsync(Ct);

        result.Outcome.ShouldBe(SyncOutcome.Completed);
        result.Pushed.ShouldBe(1);
        (await device.Engine.PendingCountAsync(Ct)).ShouldBe(0);
        server.All("vendors").Cast<Vendor>().Single().Name.ShouldBe("Tim Wendelboe");
        (await device.State.GetServerVersionAsync("vendors", vendor.Id, Ct)).ShouldBe("1");
    }

    [Fact]
    public async Task Several_saves_of_one_record_are_one_push_with_the_latest_content()
    {
        var device = NewDevice();
        var vendor = await AddVendor(device, "A");
        vendor = await device.Vendors.SaveAsync(vendor with { Name = "B" }, Ct);
        await device.Vendors.SaveAsync(vendor with { Name = "C" }, Ct);

        (await device.Engine.PendingCountAsync(Ct)).ShouldBe(1);
        await device.Engine.SyncAsync(Ct);

        server.PushCalls.ShouldBe(1);
        server.All("vendors").Cast<Vendor>().Single().Name.ShouldBe("C");
    }

    [Fact]
    public async Task A_delete_is_pushed_as_a_soft_delete()
    {
        var device = NewDevice();
        var vendor = await AddVendor(device, "Gone");
        await device.Engine.SyncAsync(Ct);
        time.Advance(TimeSpan.FromMinutes(1));

        await device.Vendors.DeleteAsync(vendor.Id, Ct);
        await device.Engine.SyncAsync(Ct);

        server.All("vendors").Cast<Vendor>().Single().Deleted.ShouldBeTrue();
        server.VersionOf("vendors", vendor.Id).ShouldBe(2);
    }

    [Fact]
    public async Task Another_device_gets_the_record_and_its_edits()
    {
        var a = NewDevice("alice");
        var b = NewDevice("bob");
        var vendor = await AddVendor(a, "Original");
        await a.Engine.SyncAsync(Ct);

        var firstPull = await b.Engine.SyncAsync(Ct);

        firstPull.Pulled.ShouldBe(1);
        (await b.Vendors.GetAsync(vendor.Id, Ct))!.Name.ShouldBe("Original");

        time.Advance(TimeSpan.FromMinutes(1));
        var onB = (await b.Vendors.GetAsync(vendor.Id, Ct))!;
        await b.Vendors.SaveAsync(onB with { Name = "Renamed by Bob" }, Ct);
        await b.Engine.SyncAsync(Ct);
        time.Advance(TimeSpan.FromMinutes(1));
        await a.Engine.SyncAsync(Ct);

        (await a.Vendors.GetAsync(vendor.Id, Ct))!.Name.ShouldBe("Renamed by Bob");
    }

    [Fact]
    public async Task A_second_pull_with_nothing_new_applies_nothing()
    {
        var a = NewDevice();
        var b = NewDevice("bob");
        await AddVendor(a, "One");
        await a.Engine.SyncAsync(Ct);
        await b.Engine.SyncAsync(Ct);

        var again = await b.Engine.SyncAsync(Ct);

        again.Pulled.ShouldBe(0);
    }

    [Fact]
    public async Task Deleted_records_come_through_a_pull()
    {
        var a = NewDevice();
        var b = NewDevice("bob");
        var vendor = await AddVendor(a, "Soon gone");
        await a.Engine.SyncAsync(Ct);
        await b.Engine.SyncAsync(Ct);
        time.Advance(TimeSpan.FromMinutes(1));
        await a.Vendors.DeleteAsync(vendor.Id, Ct);
        await a.Engine.SyncAsync(Ct);

        await b.Engine.SyncAsync(Ct);

        (await b.Vendors.GetAsync(vendor.Id, Ct)).ShouldBeNull();
        (await b.Vendors.GetIncludingDeletedAsync(vendor.Id, Ct))!.Deleted.ShouldBeTrue();
    }

    [Fact]
    public async Task Pulling_pages_through_records_that_share_a_timestamp()
    {
        var b = NewDevice("bob", pageSize: 2);
        for (var i = 0; i < 5; i++)
        {
            server.Seed("vendors", new Vendor { Name = $"V{i}", HouseholdId = "h1" });
        }

        var result = await b.Engine.SyncAsync(Ct);

        result.Pulled.ShouldBe(5);
        (await b.Vendors.ListAsync(Ct)).Count.ShouldBe(5);
    }

    [Fact]
    public async Task An_offline_edit_wins_over_another_devices_newer_edit()
    {
        var a = NewDevice("alice");
        var b = NewDevice("bob");
        var vendor = await AddVendor(a, "Shared");
        await a.Engine.SyncAsync(Ct);
        await b.Engine.SyncAsync(Ct);
        time.Advance(TimeSpan.FromMinutes(1));
        await a.Vendors.SaveAsync((await a.Vendors.GetAsync(vendor.Id, Ct))! with { Name = "Alice edit" }, Ct);
        await a.Engine.SyncAsync(Ct);
        time.Advance(TimeSpan.FromMinutes(1));

        // Bob edited from a stale copy: last writer wins, so his edit goes out on top of Alice's.
        var onB = (await b.Vendors.GetAsync(vendor.Id, Ct))!;
        await b.Vendors.SaveAsync(onB with { Name = "Bob edit" }, Ct);
        var result = await b.Engine.SyncAsync(Ct);

        result.Conflicts.ShouldBe(1);
        (await b.Vendors.GetAsync(vendor.Id, Ct))!.Name.ShouldBe("Bob edit");
        server.All("vendors").Cast<Vendor>().Single().Name.ShouldBe("Bob edit");
    }

    [Fact]
    public async Task A_pull_does_not_overwrite_a_change_that_could_not_be_pushed()
    {
        var device = NewDevice();
        var vendor = await AddVendor(device, "Mine");
        await device.Engine.SyncAsync(Ct);
        time.Advance(TimeSpan.FromMinutes(1));
        server.Seed("vendors", vendor with { Name = "Theirs" });
        server.RejectIds.Add(vendor.Id);
        await device.Vendors.SaveAsync((await device.Vendors.GetAsync(vendor.Id, Ct))! with { Name = "Mine 2" }, Ct);

        var result = await device.Engine.SyncAsync(Ct);

        result.Failed.ShouldBe(1);
        (await device.Vendors.GetAsync(vendor.Id, Ct))!.Name.ShouldBe("Mine 2");
    }

    [Fact]
    public async Task Last_writer_wins_on_a_conflict_and_the_overwritten_copy_is_logged()
    {
        var device = NewDevice();
        var vendor = await AddVendor(device, "Mine");
        await device.Engine.SyncAsync(Ct);
        time.Advance(TimeSpan.FromMinutes(1));
        server.Seed("vendors", vendor with { Name = "Theirs" });
        time.Advance(TimeSpan.FromMinutes(1));
        await device.Vendors.SaveAsync((await device.Vendors.GetAsync(vendor.Id, Ct))! with { Name = "Mine, edited" }, Ct);

        var result = await device.Engine.SyncAsync(Ct);

        result.Conflicts.ShouldBe(1);
        server.All("vendors").Cast<Vendor>().Single().Name.ShouldBe("Mine, edited");
        var log = await device.State.ConflictsAsync(Ct);
        log.Single().OverwrittenServerCopy.ShouldContain("Theirs");
    }

    [Fact]
    public async Task Offline_keeps_the_outbox_and_the_next_sync_catches_up()
    {
        var device = NewDevice();
        await AddVendor(device, "Offline made");
        server.Offline = true;

        var offline = await device.Engine.SyncAsync(Ct);

        offline.Outcome.ShouldBe(SyncOutcome.Offline);
        (await device.Engine.PendingCountAsync(Ct)).ShouldBe(1);

        server.Offline = false;
        var online = await device.Engine.SyncAsync(Ct);

        online.Outcome.ShouldBe(SyncOutcome.Completed);
        (await device.Engine.PendingCountAsync(Ct)).ShouldBe(0);
        server.All("vendors").Count().ShouldBe(1);
    }

    [Fact]
    public async Task A_refused_record_stays_queued_with_its_error_and_the_rest_still_sync()
    {
        var device = NewDevice();
        var bad = await AddVendor(device, "Refused");
        await AddVendor(device, "Fine");
        server.RejectIds.Add(bad.Id);

        var result = await device.Engine.SyncAsync(Ct);

        result.Failed.ShouldBe(1);
        result.Pushed.ShouldBe(1);
        var pending = (await device.State.PendingAsync(Ct)).Single();
        pending.EntityId.ShouldBe(bad.Id);
        pending.Attempts.ShouldBe(1);
        pending.LastError.ShouldBe("no access");
    }

    [Fact]
    public async Task A_remote_change_does_not_come_back_as_a_local_change()
    {
        var a = NewDevice();
        var b = NewDevice("bob");
        await AddVendor(a, "From A");
        await a.Engine.SyncAsync(Ct);

        await b.Engine.SyncAsync(Ct);

        (await b.Engine.PendingCountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_remote_change_bumps_the_local_version_so_open_edits_conflict()
    {
        var a = NewDevice();
        var b = NewDevice("bob");
        var vendor = await AddVendor(a, "V");
        await a.Engine.SyncAsync(Ct);
        await b.Engine.SyncAsync(Ct);
        var openOnB = (await b.Vendors.GetAsync(vendor.Id, Ct))!;
        time.Advance(TimeSpan.FromMinutes(1));
        await a.Vendors.SaveAsync((await a.Vendors.GetAsync(vendor.Id, Ct))! with { Name = "V2" }, Ct);
        await a.Engine.SyncAsync(Ct);
        await b.Engine.SyncAsync(Ct);

        await Should.ThrowAsync<ConflictException>(() => b.Vendors.SaveAsync(openOnB with { Name = "stale" }, Ct));
    }

    [Fact]
    public async Task Only_one_sync_runs_at_a_time()
    {
        var device = NewDevice();
        await AddVendor(device, "X");
        var gate = new TaskCompletionSource();
        var slow = new SlowTransport(server, gate.Task);
        var engine = new SyncEngine([new SyncTable<Vendor>("vendors", device.Vendors, device.State, slow, time)], device.State);

        var first = engine.SyncAsync(Ct);
        var second = await engine.SyncAsync(Ct);
        gate.SetResult();
        await first;

        second.Outcome.ShouldBe(SyncOutcome.AlreadyRunning);
    }

    private sealed class SlowTransport(ISyncTransport inner, Task release) : ISyncTransport
    {
        public async Task<PushResult<T>> PushAsync<T>(string table, T entity, string? ifMatchVersion, CancellationToken cancellationToken = default)
            where T : SyncEntity
        {
            await release;
            return await inner.PushAsync(table, entity, ifMatchVersion, cancellationToken);
        }

        public Task<PullPage<T>> PullAsync<T>(string table, SyncWatermark? after, int pageSize, CancellationToken cancellationToken = default)
            where T : SyncEntity => inner.PullAsync<T>(table, after, pageSize, cancellationToken);
    }
}
