using Dialed.Api.Friends;

namespace Dialed.Api.Tests;

/// <summary>What every friend store does (#38, #74): unique friend codes, follows both ways, blocks, and forgetting a person.</summary>
public abstract class FriendStoreContract
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected static readonly DateTimeOffset Start = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    protected abstract IFriendStore NewStore();

    protected static string NewUser() => Guid.NewGuid().ToString("N");

    protected static string NewCode() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    public async Task A_person_keeps_one_code_that_finds_them_whatever_the_case()
    {
        var store = NewStore();
        var alice = NewUser();
        var code = NewCode();

        (await store.GetOrCreateCodeAsync(alice, () => code, Ct)).ShouldBe(code);
        (await store.GetOrCreateCodeAsync(alice, NewCode, Ct)).ShouldBe(code);

        (await store.FindUserByCodeAsync(code, Ct)).ShouldBe(alice);
        (await store.FindUserByCodeAsync($" {code.ToLowerInvariant()} ", Ct)).ShouldBe(alice);
        (await store.FindUserByCodeAsync(NewCode(), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_taken_code_is_never_handed_out_twice()
    {
        var store = NewStore();
        var taken = NewCode();
        var fresh = NewCode();
        await store.GetOrCreateCodeAsync(NewUser(), () => taken, Ct);
        var draws = new Queue<string>([taken, fresh]);

        (await store.GetOrCreateCodeAsync(NewUser(), draws.Dequeue, Ct)).ShouldBe(fresh);
    }

    [Fact]
    public async Task Rotating_a_code_stops_the_old_one()
    {
        var store = NewStore();
        var alice = NewUser();
        var old = await store.GetOrCreateCodeAsync(alice, NewCode, Ct);
        var next = NewCode();

        (await store.RotateCodeAsync(alice, () => next, Ct)).ShouldBe(next);

        (await store.FindUserByCodeAsync(old, Ct)).ShouldBeNull();
        (await store.FindUserByCodeAsync(next, Ct)).ShouldBe(alice);
        (await store.GetOrCreateCodeAsync(alice, NewCode, Ct)).ShouldBe(next);
    }

    [Fact]
    public async Task Follows_are_found_both_ways_oldest_first_and_can_end()
    {
        var store = NewStore();
        var (alice, bob, carol) = (NewUser(), NewUser(), NewUser());
        await store.SaveFollowAsync(new Follow(carol, bob, FollowStatus.Accepted, Start.AddMinutes(2)), Ct);
        await store.SaveFollowAsync(new Follow(alice, bob, FollowStatus.Pending, Start), Ct);
        await store.SaveFollowAsync(new Follow(alice, carol, FollowStatus.Pending, Start.AddMinutes(1)), Ct);

        (await store.FindFollowAsync(alice, bob, Ct)).ShouldBe(new Follow(alice, bob, FollowStatus.Pending, Start));
        (await store.FindFollowAsync(bob, alice, Ct)).ShouldBeNull();
        (await store.FollowsAsync(alice, asFollowee: false, Ct)).Select(f => f.FolloweeId).ShouldBe([bob, carol]);
        (await store.FollowsAsync(bob, asFollowee: true, Ct)).Select(f => f.FollowerId).ShouldBe([alice, carol]);

        await store.SaveFollowAsync(new Follow(alice, bob, FollowStatus.Accepted, Start), Ct); // accepted
        (await store.FindFollowAsync(alice, bob, Ct))!.Status.ShouldBe(FollowStatus.Accepted);

        (await store.RemoveFollowAsync(alice, bob, Ct)).ShouldBeTrue();
        (await store.RemoveFollowAsync(alice, bob, Ct)).ShouldBeFalse();
        (await store.FollowsAsync(bob, asFollowee: true, Ct)).Select(f => f.FollowerId).ShouldBe([carol]);
    }

    [Fact]
    public async Task Blocks_are_one_way_and_can_be_lifted()
    {
        var store = NewStore();
        var (alice, bob) = (NewUser(), NewUser());

        (await store.AddBlockAsync(alice, bob, Ct)).ShouldBeTrue();
        (await store.AddBlockAsync(alice, bob, Ct)).ShouldBeFalse();

        (await store.IsBlockedAsync(alice, bob, Ct)).ShouldBeTrue();
        (await store.IsBlockedAsync(bob, alice, Ct)).ShouldBeFalse();
        (await store.BlockedByAsync(alice, Ct)).ShouldBe([bob]);

        (await store.RemoveBlockAsync(alice, bob, Ct)).ShouldBeTrue();
        (await store.RemoveBlockAsync(alice, bob, Ct)).ShouldBeFalse();
        (await store.IsBlockedAsync(alice, bob, Ct)).ShouldBeFalse();
        (await store.BlockedByAsync(alice, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Removing_a_person_forgets_their_code_follows_and_blocks_both_ways()
    {
        var store = NewStore();
        var (alice, bob, carol) = (NewUser(), NewUser(), NewUser());
        var code = await store.GetOrCreateCodeAsync(alice, NewCode, Ct);
        await store.GetOrCreateCodeAsync(bob, NewCode, Ct);
        await store.SaveFollowAsync(new Follow(alice, bob, FollowStatus.Accepted, Start), Ct);
        await store.SaveFollowAsync(new Follow(bob, alice, FollowStatus.Accepted, Start), Ct);
        await store.SaveFollowAsync(new Follow(carol, bob, FollowStatus.Accepted, Start), Ct);
        await store.AddBlockAsync(alice, carol, Ct);
        await store.AddBlockAsync(carol, alice, Ct);
        await store.AddBlockAsync(carol, bob, Ct);

        await store.RemoveUserAsync(alice, Ct);

        (await store.FindUserByCodeAsync(code, Ct)).ShouldBeNull();
        (await store.FollowsAsync(alice, asFollowee: false, Ct)).ShouldBeEmpty();
        (await store.FollowsAsync(alice, asFollowee: true, Ct)).ShouldBeEmpty();
        (await store.FollowsAsync(bob, asFollowee: false, Ct)).ShouldBeEmpty();
        (await store.FollowsAsync(bob, asFollowee: true, Ct)).Select(f => f.FollowerId).ShouldBe([carol]);
        (await store.BlockedByAsync(alice, Ct)).ShouldBeEmpty();
        (await store.BlockedByAsync(carol, Ct)).ShouldBe([bob]);
        (await store.GetOrCreateCodeAsync(alice, NewCode, Ct)).ShouldNotBe(code);
    }
}

public sealed class InMemoryFriendStoreTests : FriendStoreContract
{
    protected override IFriendStore NewStore() => new InMemoryFriendStore();
}

/// <summary>The Cosmos DB friend store against a real Cosmos DB; skips without <c>COSMOS_CONNECTION_STRING</c>.</summary>
public sealed class CosmosFriendStoreTests : FriendStoreContract, IAsyncDisposable
{
    private readonly CosmosTestDatabase database = new();

    protected override IFriendStore NewStore() => new CosmosFriendStore(database.New());

    public ValueTask DisposeAsync() => database.DisposeAsync();

    [Fact]
    public async Task Codes_follows_and_blocks_hold_for_a_new_instance()
    {
        var (alice, bob) = (NewUser(), NewUser());
        var code = await NewStore().GetOrCreateCodeAsync(alice, NewCode, Ct);
        await NewStore().SaveFollowAsync(new Follow(bob, alice, FollowStatus.Pending, Start), Ct);
        await NewStore().AddBlockAsync(alice, bob, Ct);

        var fresh = NewStore();
        (await fresh.FindUserByCodeAsync(code, Ct)).ShouldBe(alice);
        (await fresh.FollowsAsync(alice, asFollowee: true, Ct)).Single().FollowerId.ShouldBe(bob);
        (await fresh.IsBlockedAsync(alice, bob, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Two_instances_asking_for_a_first_code_agree_on_one()
    {
        var alice = NewUser();

        var codes = await Task.WhenAll(NewStore().GetOrCreateCodeAsync(alice, NewCode, Ct), NewStore().GetOrCreateCodeAsync(alice, NewCode, Ct));

        codes.Distinct().Count().ShouldBe(1);
        (await NewStore().FindUserByCodeAsync(codes[0], Ct)).ShouldBe(alice);
    }
}
