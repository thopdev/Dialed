using Dialed.Client.Data;
using Dialed.Client.Sync;
using Dialed.Domain.Inventory;
using Dialed.Domain.Model;

namespace Dialed.Client.Tests;

/// <summary>Two phones in one household changing things offline, then syncing (ADR-0011: editable records last writer wins, brews never collide).</summary>
public sealed class TwoDeviceSyncTests
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

    private sealed class Phone
    {
        public Phone(string user, FakeSyncServer server, TimeProvider time)
        {
            Recipes = new InMemoryRepository<Recipe>(new Context(user), time);
            Bags = new InMemoryRepository<Bag>(new Context(user), time);
            Brews = new InMemoryRepository<Brew>(new Context(user), time);
            State = new InMemorySyncState();
            Engine = new SyncEngine(
                [
                    new SyncTable<Recipe>("recipes", Recipes, State, server, time).Attach(),
                    new SyncTable<Bag>("bags", Bags, State, server, time).Attach(),
                    new SyncTable<Brew>("brews", Brews, State, server, time).Attach(),
                ],
                State);
        }

        public InMemoryRepository<Recipe> Recipes { get; }

        public InMemoryRepository<Bag> Bags { get; }

        public InMemoryRepository<Brew> Brews { get; }

        public InMemorySyncState State { get; }

        public SyncEngine Engine { get; }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTime time = new(new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeSyncServer server;
    private readonly Phone alice;
    private readonly Phone bob;

    public TwoDeviceSyncTests()
    {
        server = new FakeSyncServer(time);
        alice = new Phone("alice", server, time);
        bob = new Phone("bob", server, time);
    }

    private async Task SyncBoth()
    {
        await alice.Engine.SyncAsync(Ct);
        await bob.Engine.SyncAsync(Ct);
        await alice.Engine.SyncAsync(Ct);
        await bob.Engine.SyncAsync(Ct);
    }

    private async Task<(Recipe OnAlice, Recipe OnBob)> SharedRecipe()
    {
        var recipe = await alice.Recipes.SaveAsync(new Recipe { MethodCode = "v60", Name = "Morning V60" }, Ct);
        await SyncBoth();
        return ((await alice.Recipes.GetAsync(recipe.Id, Ct))!, (await bob.Recipes.GetAsync(recipe.Id, Ct))!);
    }

    [Theory]
    [InlineData(true, "Bob's tweak")]
    [InlineData(false, "Alice's tweak")]
    public async Task Two_phones_editing_the_same_recipe_offline_end_up_the_same_and_the_last_to_sync_wins(bool aliceSyncsFirst, string expectedName)
    {
        var (onAlice, onBob) = await SharedRecipe();
        server.Offline = true;
        await alice.Recipes.SaveAsync(onAlice with { Name = "Alice's tweak" }, Ct);
        time.Advance(TimeSpan.FromMinutes(1));
        await bob.Recipes.SaveAsync(onBob with { Name = "Bob's tweak" }, Ct);
        server.Offline = false;

        var (first, second) = aliceSyncsFirst ? (alice, bob) : (bob, alice);
        await first.Engine.SyncAsync(Ct);
        var result = await second.Engine.SyncAsync(Ct);
        await first.Engine.SyncAsync(Ct);

        result.Conflicts.ShouldBe(1);
        (await alice.Recipes.GetAsync(onAlice.Id, Ct))!.Name.ShouldBe(expectedName);
        (await bob.Recipes.GetAsync(onAlice.Id, Ct))!.Name.ShouldBe(expectedName);
        server.All("recipes").Cast<Recipe>().Single().Name.ShouldBe(expectedName);
    }

    [Fact]
    public async Task The_overwritten_version_is_kept_in_the_conflict_log_of_the_phone_that_won()
    {
        var (onAlice, onBob) = await SharedRecipe();
        server.Offline = true;
        await alice.Recipes.SaveAsync(onAlice with { Name = "Alice's tweak" }, Ct);
        await bob.Recipes.SaveAsync(onBob with { Name = "Bob's tweak" }, Ct);
        server.Offline = false;

        await alice.Engine.SyncAsync(Ct);
        await bob.Engine.SyncAsync(Ct);

        var conflict = (await bob.State.ConflictsAsync(Ct)).Single();
        conflict.Table.ShouldBe("recipes");
        conflict.EntityId.ShouldBe(onAlice.Id);
        conflict.OverwrittenServerCopy.ShouldContain("Alice's tweak");
        (await alice.State.ConflictsAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Edits_to_different_recipes_never_conflict()
    {
        var a = await alice.Recipes.SaveAsync(new Recipe { MethodCode = "v60", Name = "A" }, Ct);
        var b = await bob.Recipes.SaveAsync(new Recipe { MethodCode = "aeropress", Name = "B" }, Ct);

        await SyncBoth();

        (await alice.Recipes.ListAsync(Ct)).Select(r => r.Name).Order().ShouldBe(["A", "B"]);
        (await bob.Recipes.ListAsync(Ct)).Select(r => r.Name).Order().ShouldBe(["A", "B"]);
        (await alice.State.ConflictsAsync(Ct)).ShouldBeEmpty();
        (await bob.State.ConflictsAsync(Ct)).ShouldBeEmpty();
        a.Id.ShouldNotBe(b.Id);
    }

    [Fact]
    public async Task Brewing_from_the_same_bag_on_two_phones_offline_leaves_the_right_amount_in_the_bag()
    {
        var bag = await alice.Bags.SaveAsync(new Bag { BeanId = "bean1", WeightG = 250m }, Ct);
        await SyncBoth();
        server.Offline = true;
        await alice.Brews.SaveAsync(new Brew { BagId = bag.Id, MethodCode = "v60", DoseG = 18m }, Ct);
        await alice.Brews.SaveAsync(new Brew { BagId = bag.Id, MethodCode = "v60", DoseG = 16m }, Ct);
        await bob.Brews.SaveAsync(new Brew { BagId = bag.Id, MethodCode = "aeropress", DoseG = 14m }, Ct);
        server.Offline = false;

        await SyncBoth();

        foreach (var phone in new[] { alice, bob })
        {
            var brews = await phone.Brews.ListAsync(Ct);
            brews.Count.ShouldBe(3);
            BagInventory.Remaining(250m, brews.Where(b => b.BagId == bag.Id).Select(b => b.DoseG), []).ShouldBe(202m);
            (await phone.State.ConflictsAsync(Ct)).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task A_recipe_deleted_on_one_phone_and_edited_on_the_other_converges_on_the_last_sync()
    {
        var (onAlice, onBob) = await SharedRecipe();
        server.Offline = true;
        await alice.Recipes.DeleteAsync(onAlice.Id, Ct);
        await bob.Recipes.SaveAsync(onBob with { Name = "Edited" }, Ct);
        server.Offline = false;

        await alice.Engine.SyncAsync(Ct);
        await bob.Engine.SyncAsync(Ct);
        await alice.Engine.SyncAsync(Ct);

        // Bob synced last, so his edit stands and the recipe is back for everyone.
        (await alice.Recipes.GetAsync(onAlice.Id, Ct))!.Name.ShouldBe("Edited");
        (await bob.Recipes.GetAsync(onAlice.Id, Ct))!.Name.ShouldBe("Edited");
    }
}
