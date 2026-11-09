using System.Text.Json.Nodes;
using Dialed.Api.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dialed.Api.Tests;

public sealed class PurgeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    private static Task<StoreResult> Put(InMemorySyncStore store, string table, string id, bool deleted, string? ifMatch = null) =>
        store.PushAsync("h", "alice", table, id, new JsonObject { ["id"] = id, ["deleted"] = deleted }, ifMatch, Ct);

    private static async Task<int> Count(InMemorySyncStore store, string table) =>
        (await store.PullAsync("h", "alice", table, null, null, 500, Ct)).Rows.Count;

    [Fact]
    public async Task Records_deleted_more_than_thirty_days_ago_are_removed_and_counted_per_table()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemorySyncStore(time);
        await Put(store, "beans", "old", deleted: true);
        await Put(store, "bags", "old-bag", deleted: true);
        time.Advance(TimeSpan.FromDays(31));
        await Put(store, "beans", "recent", deleted: true);
        await Put(store, "beans", "alive", deleted: false);

        var counts = await store.PurgeDeletedAsync(time.GetUtcNow() - SyncPurgeWorker.Retention, Ct);

        counts["beans"].ShouldBe(1);
        counts["bags"].ShouldBe(1);
        (await Count(store, "beans")).ShouldBe(2);
        (await Count(store, "bags")).ShouldBe(0);
    }

    [Fact]
    public async Task Old_records_that_are_not_deleted_are_kept()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var store = new InMemorySyncStore(time);
        await Put(store, "beans", "alive", deleted: false);
        time.Advance(TimeSpan.FromDays(90));

        var counts = await store.PurgeDeletedAsync(time.GetUtcNow() - SyncPurgeWorker.Retention, Ct);

        counts.ShouldBeEmpty();
        (await Count(store, "beans")).ShouldBe(1);
    }

    [Fact]
    public async Task Purging_twice_is_harmless_and_the_worker_logs_through_the_same_path()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var store = new InMemorySyncStore(time);
        await Put(store, "beans", "gone", deleted: true);
        time.Advance(TimeSpan.FromDays(31));
        var worker = new SyncPurgeWorker(store, time, NullLogger<SyncPurgeWorker>.Instance);

        await worker.RunOnceAsync(Ct);
        await worker.RunOnceAsync(Ct);

        (await Count(store, "beans")).ShouldBe(0);
    }

    [Fact]
    public async Task A_record_restored_after_deletion_is_not_purged()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var store = new InMemorySyncStore(time);
        await Put(store, "beans", "b", deleted: true);
        time.Advance(TimeSpan.FromDays(20));
        await Put(store, "beans", "b", deleted: false, ifMatch: "1");
        time.Advance(TimeSpan.FromDays(20));

        await store.PurgeDeletedAsync(time.GetUtcNow() - SyncPurgeWorker.Retention, Ct);

        (await Count(store, "beans")).ShouldBe(1);
    }
}
