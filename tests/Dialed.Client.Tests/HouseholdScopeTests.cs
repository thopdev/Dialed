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
    public async Task Leaving_or_being_removed_purges_what_others_shared_forgets_positions_and_keeps_own_records()
    {
        var (alices, bobs) = await BothHaveOneAndShare();
        (await bobState.GetWatermarkAsync("recipes", Ct)).ShouldNotBeNull();
        bobContext.HouseholdId = "h-bob";

        var changed = await bob.ReconcileHouseholdAsync("h-bob", Ct);

        changed.ShouldBeTrue();
        (await bobRecipes.GetAsync(alices.Id, Ct)).ShouldBeNull();
        (await bobRecipes.GetIncludingDeletedAsync(alices.Id, Ct)).ShouldBeNull();
        var own = await bobRecipes.GetAsync(bobs.Id, Ct);
        own.ShouldNotBeNull();
        own.HouseholdId.ShouldBe("h-bob");
        (await bobState.GetWatermarkAsync("recipes", Ct)).ShouldBeNull();
        (await bobState.GetHouseholdAsync(Ct)).ShouldBe("h-bob");
    }

    [Fact]
    public async Task Joining_another_household_purges_the_old_one_and_the_next_sync_pulls_the_new_one()
    {
        var (alices, bobs) = await BothHaveOneAndShare();
        var carolRepo = new InMemoryRepository<Recipe>(new Context("carol", "h2"), time);
        var carolState = new InMemorySyncState();
        var carolEngine = new SyncEngine([new SyncTable<Recipe>("recipes", carolRepo, carolState, server, time).Attach()], carolState);
        var carolsRecipe = await carolRepo.SaveAsync(new Recipe { MethodCode = "aeropress", Name = "Carol's" }, Ct);
        await carolEngine.SyncAsync(Ct);
        bobContext.HouseholdId = "h2";

        await bob.SwitchHouseholdAsync("h2", Ct);
        await Task.Delay(5, Ct);
        await bob.SyncAsync(Ct);

        (await bobRecipes.GetAsync(alices.Id, Ct)).ShouldBeNull();
        (await bobRecipes.GetAsync(bobs.Id, Ct)).ShouldNotBeNull();
        (await bobRecipes.GetAsync(carolsRecipe.Id, Ct)).ShouldNotBeNull();
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
