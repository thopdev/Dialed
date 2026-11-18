using System.Text.Json.Nodes;
using Dialed.Api.Sync;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dialed.Api.Tests;

/// <summary>
/// What every sync store does (#38, #157 to #161): watermark pulls in pages, versions, soft deletes and the purge, the visibility of private items,
/// and one household never touching another's records. The API tests (<see cref="SyncApiTests"/>) run on the in-memory store; this contract
/// runs the same rules on both stores.
/// </summary>
public abstract class SyncStoreContract
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected static readonly DateTimeOffset Start = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    protected TestClock Time { get; } = new(Start);

    protected abstract ISyncStore NewStore();

    protected static string NewHousehold() => $"h-{Guid.NewGuid():N}";

    protected static Task<StoreResult> Put(ISyncStore store, string household, string user, string table, string id, bool deleted = false, string? ifMatch = null, int? visibility = null, string? name = null)
    {
        var entity = new JsonObject { ["id"] = id, ["deleted"] = deleted, ["name"] = name ?? id };
        if (visibility is { } v)
        {
            entity["visibility"] = v;
        }

        return store.PushAsync(household, user, table, id, entity, ifMatch, Ct);
    }

    protected static async Task<List<SyncRow>> PullAll(ISyncStore store, string household, string viewer, string table, int pageSize = 500)
    {
        var all = new List<SyncRow>();
        DateTimeOffset? after = null;
        string? afterId = null;
        while (true)
        {
            var (rows, hasMore) = await store.PullAsync(household, viewer, table, after, afterId, pageSize, Ct);
            all.AddRange(rows);
            if (!hasMore)
            {
                return all;
            }

            rows.Count.ShouldBe(pageSize);
            (after, afterId) = (rows[^1].UpdatedAt, rows[^1].Id);
        }
    }

    [Fact]
    public async Task A_new_record_is_stored_with_version_one_and_the_server_fields()
    {
        var store = NewStore();
        var household = NewHousehold();

        var result = await Put(store, household, "alice", "beans", "b1");

        result.Outcome.ShouldBe(StoreOutcome.Stored);
        result.Row!.Version.ShouldBe(1);
        result.Row.UpdatedAt.ShouldBe(Start);
        var stored = (await store.GetAsync(household, "beans", "b1", Ct))!;
        stored.Version.ShouldBe(1);
        stored.HouseholdId.ShouldBe(household);
        stored.UpdatedAt.ShouldBe(Start);
        stored.Entity["householdId"]!.GetValue<string>().ShouldBe(household);
        stored.Entity["createdBy"]!.GetValue<string>().ShouldBe("alice");
        stored.Entity["updatedBy"]!.GetValue<string>().ShouldBe("alice");
        stored.Entity["version"]!.GetValue<int>().ShouldBe(1);
        stored.Entity["name"]!.GetValue<string>().ShouldBe("b1");
        (await store.GetAsync(household, "bags", "b1", Ct)).ShouldBeNull(); // per table
    }

    [Fact]
    public async Task An_update_needs_the_current_version_and_keeps_the_creation_details()
    {
        var store = NewStore();
        var household = NewHousehold();
        await Put(store, household, "alice", "beans", "b1");
        Time.Advance(TimeSpan.FromMinutes(5));

        var stale = await Put(store, household, "bob", "beans", "b1", ifMatch: "7");
        stale.Outcome.ShouldBe(StoreOutcome.VersionMismatch);
        stale.Row!.Version.ShouldBe(1);
        (await Put(store, household, "bob", "beans", "b1")).Outcome.ShouldBe(StoreOutcome.VersionMismatch); // exists, no If-Match

        var updated = await Put(store, household, "bob", "beans", "b1", ifMatch: "1", name: "renamed");

        updated.Outcome.ShouldBe(StoreOutcome.Stored);
        updated.Row!.Version.ShouldBe(2);
        var stored = (await store.GetAsync(household, "beans", "b1", Ct))!;
        stored.Version.ShouldBe(2);
        stored.UpdatedAt.ShouldBe(Start.AddMinutes(5));
        stored.Entity["name"]!.GetValue<string>().ShouldBe("renamed");
        stored.Entity["createdBy"]!.GetValue<string>().ShouldBe("alice");
        stored.Entity["updatedBy"]!.GetValue<string>().ShouldBe("bob");
        stored.Entity["createdAt"]!.GetValue<DateTimeOffset>().ShouldBe(Start);
    }

    [Fact]
    public async Task A_bad_push_is_refused()
    {
        var store = NewStore();
        var household = NewHousehold();

        (await store.PushAsync(household, "alice", "beans", "b1", new JsonObject { ["id"] = "other" }, null, Ct)).Outcome.ShouldBe(StoreOutcome.Invalid);
        (await Put(store, household, "alice", "beans", "new", ifMatch: "1")).Outcome.ShouldBe(StoreOutcome.Invalid); // unknown version
        (await store.PushAsync(household, "alice", "beans", "b2", new JsonObject { ["id"] = "b2", ["householdId"] = "h-someone-else" }, null, Ct))
            .Outcome.ShouldBe(StoreOutcome.Forbidden);
        (await store.GetAsync(household, "beans", "b2", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task One_household_never_sees_or_changes_another_households_records()
    {
        var store = NewStore();
        var a = NewHousehold();
        var b = NewHousehold();
        await Put(store, a, "alice", "beans", "same-id", name: "alice's");

        // The same id in another household is a separate record: it starts at version 1 and leaves the first alone.
        var other = await Put(store, b, "mallory", "beans", "same-id", name: "mallory's");
        other.Outcome.ShouldBe(StoreOutcome.Stored);
        other.Row!.Version.ShouldBe(1);

        (await PullAll(store, b, "mallory", "beans")).Select(r => r.Entity["name"]!.GetValue<string>()).ShouldBe(["mallory's"]);
        (await PullAll(store, a, "alice", "beans")).Select(r => r.Entity["name"]!.GetValue<string>()).ShouldBe(["alice's"]);
        (await store.GetAsync(a, "beans", "same-id", Ct))!.Version.ShouldBe(1);
    }

    [Fact]
    public async Task Pulls_page_through_changes_in_update_order_from_the_watermark()
    {
        var store = NewStore();
        var household = NewHousehold();

        // Three records share a time stamp, so the id breaks the tie.
        foreach (var id in new[] { "c", "a", "b" })
        {
            await Put(store, household, "alice", "beans", id);
        }

        Time.Advance(TimeSpan.FromSeconds(1));
        await Put(store, household, "alice", "beans", "z");
        Time.Advance(TimeSpan.FromSeconds(1));
        await Put(store, household, "alice", "beans", "a", ifMatch: "1"); // edited: moves to the end
        await Put(store, household, "alice", "bags", "bag-1");

        var all = await PullAll(store, household, "alice", "beans", pageSize: 2);
        all.Select(r => r.Id).ShouldBe(["b", "c", "z", "a"]);

        // From a watermark only what changed after it comes back.
        var (rows, hasMore) = await store.PullAsync(household, "alice", "beans", Start, "b", 10, Ct);
        rows.Select(r => r.Id).ShouldBe(["c", "z", "a"]);
        hasMore.ShouldBeFalse();
        var (later, _) = await store.PullAsync(household, "alice", "beans", Start.AddSeconds(1), null, 10, Ct);
        later.Select(r => r.Id).ShouldBe(["z", "a"]);
        (await store.PullAsync(household, "alice", "beans", Start.AddSeconds(2), "a", 10, Ct)).Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Deleted_records_are_still_pulled_so_devices_learn_about_them()
    {
        var store = NewStore();
        var household = NewHousehold();
        await Put(store, household, "alice", "beans", "b1");
        await Put(store, household, "alice", "beans", "b1", deleted: true, ifMatch: "1");

        var rows = await PullAll(store, household, "bob", "beans");

        rows.Single().Entity["deleted"]!.GetValue<bool>().ShouldBeTrue();
        rows.Single().Version.ShouldBe(2);
    }

    [Fact]
    public async Task Someone_elses_private_record_is_left_out_of_pulls_and_cannot_be_overwritten()
    {
        var store = NewStore();
        var household = NewHousehold();
        await Put(store, household, "alice", "beans", "secret", visibility: VisibilityRules.Private);
        await Put(store, household, "alice", "beans", "shared", visibility: VisibilityRules.Household);
        await Put(store, household, "alice", "beans", "by-name", name: "x");
        await store.PushAsync(household, "alice", "beans", "named-private", new JsonObject { ["id"] = "named-private", ["visibility"] = "Private" }, null, Ct);

        (await PullAll(store, household, "alice", "beans")).Count.ShouldBe(4);
        (await PullAll(store, household, "bob", "beans", pageSize: 1)).Select(r => r.Id).Order().ShouldBe(["by-name", "shared"]);

        (await Put(store, household, "bob", "beans", "secret", ifMatch: "1")).Outcome.ShouldBe(StoreOutcome.Forbidden);
        (await Put(store, household, "bob", "beans", "secret")).Outcome.ShouldBe(StoreOutcome.Forbidden); // not even a version hint
        (await store.GetAsync(household, "beans", "secret", Ct))!.Entity["updatedBy"]!.GetValue<string>().ShouldBe("alice");
    }

    [Fact]
    public async Task Shared_lists_follow_visibility_newest_first_across_households()
    {
        var store = NewStore();
        var home = NewHousehold();
        var club = NewHousehold();
        await Put(store, home, "alice", "beans", "private", visibility: VisibilityRules.Private);
        Time.Advance(TimeSpan.FromMinutes(1));
        await Put(store, home, "alice", "beans", "household", visibility: VisibilityRules.Household);
        Time.Advance(TimeSpan.FromMinutes(1));
        await Put(store, home, "alice", "beans", "friends", visibility: VisibilityRules.Friends);
        Time.Advance(TimeSpan.FromMinutes(1));
        await Put(store, home, "alice", "beans", "public", visibility: VisibilityRules.Public);
        await Put(store, home, "alice", "beans", "gone", visibility: VisibilityRules.Public, deleted: true);
        await Put(store, home, "bob", "beans", "bobs", visibility: VisibilityRules.Public);
        Time.Advance(TimeSpan.FromMinutes(1));
        await Put(store, club, "alice", "beans", "club", visibility: VisibilityRules.Friends);

        (await store.ListSharedAsync(home, "alice", "beans", Relationship.HouseholdMember, 0, 10, Ct)).Rows.Select(r => r.Id).ShouldBe(["public", "friends", "household"]);
        (await store.ListSharedAsync(home, "alice", "beans", Relationship.AcceptedFollower, 0, 10, Ct)).Rows.Select(r => r.Id).ShouldBe(["public", "friends"]);
        (await store.ListSharedAsync(home, "alice", "beans", Relationship.PublicViewer, 0, 10, Ct)).Rows.Select(r => r.Id).ShouldBe(["public"]);
        (await store.ListSharedAsync(home, "alice", "beans", Relationship.Blocked, 0, 10, Ct)).Total.ShouldBe(0);
        (await store.ListSharedAsync(home, "alice", "beans", Relationship.Author, 0, 10, Ct)).Total.ShouldBe(4);

        var (page, total) = await store.ListSharedAsync(
            [new SharedScope(home, Relationship.AcceptedFollower), new SharedScope(club, Relationship.AcceptedFollower)], "alice", "beans", 1, 2, Ct);
        total.ShouldBe(3);
        page.Select(r => r.Id).ShouldBe(["public", "friends"]);
    }

    [Fact]
    public async Task Authored_lists_hold_everything_the_person_made_in_the_household()
    {
        var store = NewStore();
        var household = NewHousehold();
        await Put(store, household, "alice", "beans", "one", visibility: VisibilityRules.Private);
        Time.Advance(TimeSpan.FromSeconds(1));
        await Put(store, household, "alice", "beans", "two");
        await Put(store, household, "alice", "beans", "gone", deleted: true);
        await Put(store, household, "bob", "beans", "bobs");
        await Put(store, NewHousehold(), "alice", "beans", "elsewhere");

        (await store.ListAuthoredAsync(household, "alice", "beans", Ct)).Select(r => r.Id).ShouldBe(["one", "two"]);
    }

    [Fact]
    public async Task Public_lists_read_every_household()
    {
        var store = NewStore();
        var a = NewHousehold();
        var b = NewHousehold();
        await Put(store, a, "alice", "beans", "a-public", visibility: VisibilityRules.Public);
        await Put(store, a, "alice", "beans", "a-friends", visibility: VisibilityRules.Friends);
        await Put(store, b, "carol", "beans", "b-public", visibility: VisibilityRules.Public);
        await Put(store, b, "carol", "beans", "b-gone", visibility: VisibilityRules.Public, deleted: true);
        await Put(store, b, "carol", "recipes", "b-recipe", visibility: VisibilityRules.Public);

        var ids = (await store.ListPublicAsync("beans", Ct)).Where(r => r.HouseholdId == a || r.HouseholdId == b).Select(r => r.Id).ToList();

        ids.Order().ShouldBe(["a-public", "b-public"]);
    }

    [Fact]
    public async Task Deleting_a_household_removes_only_its_records()
    {
        var store = NewStore();
        var gone = NewHousehold();
        var kept = NewHousehold();
        await Put(store, gone, "alice", "beans", "b1");
        await Put(store, gone, "alice", "bags", "g1");
        await Put(store, kept, "carol", "beans", "b1");

        await store.DeleteHouseholdAsync(gone, Ct);

        (await PullAll(store, gone, "alice", "beans")).ShouldBeEmpty();
        (await PullAll(store, gone, "alice", "bags")).ShouldBeEmpty();
        (await PullAll(store, kept, "carol", "beans")).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Anonymising_replaces_the_author_but_keeps_the_records()
    {
        var store = NewStore();
        var household = NewHousehold();
        await Put(store, household, "alice", "beans", "hers");
        await Put(store, household, "bob", "beans", "his");
        await Put(store, household, "alice", "beans", "his", ifMatch: "1");

        await store.AnonymiseAuthorAsync(household, "alice", "former-member", Ct);

        var hers = (await store.GetAsync(household, "beans", "hers", Ct))!;
        hers.Entity["createdBy"]!.GetValue<string>().ShouldBe("former-member");
        hers.Entity["updatedBy"]!.GetValue<string>().ShouldBe("former-member");
        var his = (await store.GetAsync(household, "beans", "his", Ct))!;
        his.Entity["createdBy"]!.GetValue<string>().ShouldBe("bob");
        his.Entity["updatedBy"]!.GetValue<string>().ShouldBe("former-member");
        his.Version.ShouldBe(2);
        (await store.ListAuthoredAsync(household, "former-member", "beans", Ct)).Select(r => r.Id).ShouldBe(["hers"]);
        (await store.ListAuthoredAsync(household, "alice", "beans", Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Records_deleted_more_than_thirty_days_ago_are_purged_and_counted_per_table()
    {
        var store = NewStore();
        var household = NewHousehold();
        await Put(store, household, "alice", "beans", "old", deleted: true);
        await Put(store, household, "alice", "bags", "old-bag", deleted: true);
        Time.Advance(TimeSpan.FromDays(31));
        await Put(store, household, "alice", "beans", "recent", deleted: true);
        await Put(store, household, "alice", "beans", "alive");

        var counts = await store.PurgeDeletedAsync(Time.GetUtcNow() - SyncPurgeWorker.Retention, Ct);

        counts["beans"].ShouldBe(1);
        counts["bags"].ShouldBe(1);
        (await PullAll(store, household, "alice", "beans")).Count.ShouldBe(2);
        (await PullAll(store, household, "alice", "bags")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Old_records_that_are_not_deleted_or_were_restored_are_kept()
    {
        var store = NewStore();
        var household = NewHousehold();
        await Put(store, household, "alice", "beans", "alive");
        await Put(store, household, "alice", "beans", "restored", deleted: true);
        Time.Advance(TimeSpan.FromDays(20));
        await Put(store, household, "alice", "beans", "restored", ifMatch: "1");
        Time.Advance(TimeSpan.FromDays(70));

        var counts = await store.PurgeDeletedAsync(Time.GetUtcNow() - SyncPurgeWorker.Retention, Ct);

        counts.ShouldBeEmpty();
        (await PullAll(store, household, "alice", "beans")).Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_purge_worker_purges_and_running_it_twice_is_harmless()
    {
        var store = NewStore();
        var household = NewHousehold();
        await Put(store, household, "alice", "beans", "gone", deleted: true);
        Time.Advance(TimeSpan.FromDays(31));
        var worker = new SyncPurgeWorker(store, Time, NullLogger<SyncPurgeWorker>.Instance);

        await worker.RunOnceAsync(Ct);
        await worker.RunOnceAsync(Ct);

        (await PullAll(store, household, "alice", "beans")).ShouldBeEmpty();
    }
}

public sealed class InMemorySyncStoreTests : SyncStoreContract
{
    protected override ISyncStore NewStore() => new InMemorySyncStore(Time);
}

/// <summary>The Cosmos DB sync store against a real Cosmos DB; skips without <c>COSMOS_CONNECTION_STRING</c>.</summary>
public sealed class CosmosSyncStoreTests : SyncStoreContract, IAsyncDisposable
{
    private readonly CosmosTestDatabase database = new();

    protected override ISyncStore NewStore() => new CosmosSyncStore(database.NewCosmos(), Time);

    public ValueTask DisposeAsync() => database.DisposeAsync();

    [Fact]
    public async Task Records_hold_for_a_new_instance()
    {
        var household = NewHousehold();
        await Put(NewStore(), household, "alice", "beans", "b1");

        var second = NewStore();
        (await Put(second, household, "bob", "beans", "b1", ifMatch: "1")).Outcome.ShouldBe(StoreOutcome.Stored);
        (await PullAll(NewStore(), household, "alice", "beans")).Single().Version.ShouldBe(2);
    }

    [Fact]
    public async Task Two_instances_pushing_the_same_version_let_only_one_win()
    {
        var household = NewHousehold();
        await Put(NewStore(), household, "alice", "beans", "b1");
        var a = NewStore();
        var b = NewStore();

        var results = await Task.WhenAll(
            Put(a, household, "alice", "beans", "b1", ifMatch: "1", name: "from a"),
            Put(b, household, "bob", "beans", "b1", ifMatch: "1", name: "from b"));

        results.Count(r => r.Outcome == StoreOutcome.Stored).ShouldBe(1);
        results.Count(r => r.Outcome == StoreOutcome.VersionMismatch).ShouldBe(1);
        (await NewStore().GetAsync(household, "beans", "b1", Ct))!.Version.ShouldBe(2);
    }

    [Fact]
    public async Task Two_instances_creating_the_same_record_let_only_one_win()
    {
        var household = NewHousehold();
        var results = await Task.WhenAll(
            Put(NewStore(), household, "alice", "beans", "new"),
            Put(NewStore(), household, "bob", "beans", "new"));

        results.Count(r => r.Outcome == StoreOutcome.Stored).ShouldBe(1);
        results.Count(r => r.Outcome == StoreOutcome.VersionMismatch).ShouldBe(1);
    }

    [Fact]
    public async Task Deleted_records_get_a_ttl_and_restored_ones_lose_it()
    {
        var cosmos = database.NewCosmos();
        var store = new CosmosSyncStore(cosmos, Time);
        var household = NewHousehold();
        await Put(store, household, "alice", "beans", "b1", deleted: true);
        var container = await cosmos.ContainerAsync(CosmosSyncStore.Container, Ct);

        var deleted = await container.ReadItemAsync<JsonObject>(CosmosSyncStore.DocumentId("beans", "b1"), new PartitionKey(household), cancellationToken: Ct);
        deleted.Resource["ttl"]!.GetValue<int>().ShouldBe((int)CosmosSyncStore.DeletedTtl.TotalSeconds);

        await Put(store, household, "alice", "beans", "b1", ifMatch: "1");
        var restored = await container.ReadItemAsync<JsonObject>(CosmosSyncStore.DocumentId("beans", "b1"), new PartitionKey(household), cancellationToken: Ct);
        restored.Resource.ContainsKey("ttl").ShouldBeFalse();
    }
}
