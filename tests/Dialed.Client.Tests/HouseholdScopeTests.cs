using Dialed.Client.Data;
using Dialed.Client.Sync;
using Dialed.Domain.Model;

namespace Dialed.Client.Tests;

/// <summary>A device only holds the data of the household its owner is in: joining, leaving and being removed all tidy up (#40).</summary>
public sealed class HouseholdScopeTests
{
    private sealed class Context(string user, string household) : ICurrentContext
    {
        public string UserId => user;

        public string HouseholdId { get; set; } = household;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TimeProvider time = TimeProvider.System;
    private readonly FakeSyncServer server;
    private readonly Context bobContext = new("bob", "h1");
    private readonly InMemoryRepository<Recipe> aliceRecipes;
    private readonly InMemoryRepository<Recipe> bobRecipes;
    private readonly InMemorySyncState aliceState = new();
    private readonly InMemorySyncState bobState = new();
    private readonly SyncEngine alice;
    private readonly SyncEngine bob;

    public HouseholdScopeTests()
    {
        server = new FakeSyncServer(time);
        aliceRecipes = new InMemoryRepository<Recipe>(new Context("alice", "h1"), time);
        bobRecipes = new InMemoryRepository<Recipe>(bobContext, time);
        alice = new SyncEngine([new SyncTable<Recipe>("recipes", aliceRecipes, aliceState, server, time).Attach()], aliceState);
        bob = new SyncEngine([new SyncTable<Recipe>("recipes", bobRecipes, bobState, server, time).Attach()], bobState);
    }

    private async Task<(Recipe Alices, Recipe Bobs)> BothHaveOneAndShare()
    {
        var alices = await aliceRecipes.SaveAsync(new Recipe { MethodCode = "v60", Name = "Alice V60" }, Ct);
        var bobs = await bobRecipes.SaveAsync(new Recipe { MethodCode = "v60", Name = "Bob V60" }, Ct);
        await bob.ReconcileHouseholdAsync("h1", Ct);
        await alice.SyncAsync(Ct);
        await Task.Delay(5, Ct);
        await bob.SyncAsync(Ct);
        (await bobRecipes.GetAsync(alices.Id, Ct)).ShouldNotBeNull();
        return (alices, bobs);
    }

    [Fact]
    public async Task The_first_sync_only_remembers_the_household()
    {
        (await bob.ReconcileHouseholdAsync("h1", Ct)).ShouldBeFalse();
        (await bobState.GetHouseholdAsync(Ct)).ShouldBe("h1");
    }

    [Fact]
    public async Task The_same_household_changes_nothing()
    {
        var (alices, _) = await BothHaveOneAndShare();

        (await bob.ReconcileHouseholdAsync("h1", Ct)).ShouldBeFalse();

        (await bobRecipes.GetAsync(alices.Id, Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Leaving_or_being_removed_purges_the_old_household_forgets_positions_and_keeps_nothing_of_it()
    {
        var (alices, bobs) = await BothHaveOneAndShare();
        (await bobState.GetWatermarkAsync("recipes", Ct)).ShouldNotBeNull();
        bobContext.HouseholdId = "h-bob";

        var changed = await bob.ReconcileHouseholdAsync("h-bob", Ct);

        changed.ShouldBeTrue();
        (await bobRecipes.GetAsync(alices.Id, Ct)).ShouldBeNull();
        (await bobRecipes.GetIncludingDeletedAsync(alices.Id, Ct)).ShouldBeNull();
        (await bobRecipes.GetIncludingDeletedAsync(bobs.Id, Ct)).ShouldBeNull(); // his own stays on the server in the old household
        (await bobState.GetWatermarkAsync("recipes", Ct)).ShouldBeNull();
        (await bobState.GetHouseholdAsync(Ct)).ShouldBe("h-bob");
    }

    [Fact]
    public async Task Joining_another_household_purges_the_old_one_and_starts_the_next_pull_from_scratch()
    {
        var (alices, bobs) = await BothHaveOneAndShare();
        bobContext.HouseholdId = "h2";

        await bob.SwitchHouseholdAsync("h2", Ct);

        (await bobRecipes.GetAsync(alices.Id, Ct)).ShouldBeNull();
        (await bobRecipes.GetIncludingDeletedAsync(bobs.Id, Ct)).ShouldBeNull();
        (await bobState.GetWatermarkAsync("recipes", Ct)).ShouldBeNull();
        (await bobState.GetServerVersionAsync("recipes", alices.Id, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Switching_back_pulls_the_first_household_again_and_never_mixes_them()
    {
        var (alices, bobs) = await BothHaveOneAndShare();
        await bob.SyncAsync(Ct); // Bob's own recipe goes up to h1
        bobContext.HouseholdId = "h2";
        await bob.SwitchHouseholdAsync("h2", Ct);
        await bobRecipes.SaveAsync(new Recipe { MethodCode = "v60", Name = "Office" }, Ct);
        (await bobRecipes.GetAsync(alices.Id, Ct)).ShouldBeNull(); // the first household is not mixed into the second
        (await bobRecipes.GetAsync(bobs.Id, Ct)).ShouldBeNull();

        bobContext.HouseholdId = "h1";
        await bob.SwitchHouseholdAsync("h1", Ct);
        await bob.SyncAsync(Ct);

        (await bobRecipes.GetAsync(alices.Id, Ct)).ShouldNotBeNull();
        (await bobRecipes.GetAsync(bobs.Id, Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Records_made_before_signing_in_move_into_the_household_you_switch_to()
    {
        bobContext.HouseholdId = LocalContext.LocalHouseholdId;
        var local = await bobRecipes.SaveAsync(new Recipe { MethodCode = "v60", Name = "Made offline" }, Ct);
        bobContext.HouseholdId = "h2";

        await bob.SwitchHouseholdAsync("h2", Ct);

        (await bobRecipes.GetAsync(local.Id, Ct))!.HouseholdId.ShouldBe("h2");
        (await bobState.PendingAsync(Ct)).ShouldContain(e => e.EntityId == local.Id);
    }

    [Fact]
    public async Task Signing_out_does_not_wipe_the_device()
    {
        var (alices, _) = await BothHaveOneAndShare();
        bobContext.HouseholdId = LocalContext.LocalHouseholdId;

        (await bob.ReconcileHouseholdAsync(LocalContext.LocalHouseholdId, Ct)).ShouldBeFalse();

        (await bobRecipes.GetAsync(alices.Id, Ct)).ShouldNotBeNull();
    }
}
