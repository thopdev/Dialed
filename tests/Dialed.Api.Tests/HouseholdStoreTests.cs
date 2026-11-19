using Dialed.Api.Households;
using Microsoft.Azure.Cosmos;
using System.Text.Json.Nodes;

namespace Dialed.Api.Tests;

/// <summary>What every household store does (#38): households created once, invites with uses and expiry, deletion with the invites.</summary>
public abstract class HouseholdStoreContract
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected static readonly DateTimeOffset Start = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    protected TestClock Time { get; } = new(Start);

    protected abstract IHouseholdStore NewStore(IEnumerable<Household>? initial = null);

    protected static string NewId() => $"hh-{Guid.NewGuid():N}";

    protected static string NewCode() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    public async Task Ensure_creates_a_household_once_and_keeps_its_first_owner()
    {
        var store = NewStore();
        var id = NewId();

        (await store.FindAsync(id, Ct)).ShouldBeNull();
        (await store.EnsureAsync(id, "Alice's household", "alice", Ct)).ShouldBe(new Household(id, "Alice's household", "alice"));
        (await store.EnsureAsync(id, "Bob's household", "bob", Ct)).ShouldBe(new Household(id, "Alice's household", "alice"));
        (await store.FindAsync(id, Ct)).ShouldBe(new Household(id, "Alice's household", "alice"));
    }

    [Fact]
    public async Task Saving_renames_or_hands_over_a_household()
    {
        var store = NewStore();
        var id = NewId();
        await store.SaveAsync(new Household(id, "Home", "alice"), Ct);
        await store.SaveAsync(new Household(id, "Our home", "bob"), Ct);

        (await store.FindAsync(id, Ct)).ShouldBe(new Household(id, "Our home", "bob"));
    }

    [Fact]
    public async Task The_starting_households_are_there()
    {
        var id = NewId();
        var store = NewStore([new Household(id, "Alice and Bob", "alice")]);

        (await store.FindAsync(id, Ct)).ShouldBe(new Household(id, "Alice and Bob", "alice"));
    }

    [Fact]
    public async Task An_invite_is_found_without_being_used_and_taken_until_its_uses_run_out()
    {
        var store = NewStore();
        var household = NewId();
        var code = NewCode();
        await store.AddInviteAsync(new Invite(code, household, Start.AddDays(7), MaxUses: 2), Ct);

        (await store.FindInviteAsync(code, Start, Ct)).ShouldBe(new Invite(code, household, Start.AddDays(7), 2, 0));
        (await store.FindInviteAsync(code, Start, Ct))!.Uses.ShouldBe(0);
        (await store.TakeInviteAsync(code, Start, Ct))!.Uses.ShouldBe(1);
        (await store.FindInviteAsync(code, Start, Ct))!.Uses.ShouldBe(1);
        (await store.TakeInviteAsync(code, Start, Ct))!.Uses.ShouldBe(2);

        (await store.FindInviteAsync(code, Start, Ct)).ShouldBeNull();
        (await store.TakeInviteAsync(code, Start, Ct)).ShouldBeNull();
        (await store.TakeInviteAsync(NewCode(), Start, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task An_expired_invite_is_gone()
    {
        var store = NewStore();
        var household = NewId();
        var code = NewCode();
        await store.AddInviteAsync(new Invite(code, household, Start.AddDays(1)), Ct);

        (await store.FindInviteAsync(code, Start.AddDays(1), Ct)).ShouldBeNull();
        (await store.TakeInviteAsync(code, Start.AddDays(1), Ct)).ShouldBeNull();
        (await store.ListInvitesAsync(household, Start.AddDays(1), Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Open_invites_are_listed_per_household_soonest_to_expire_first_and_can_be_revoked()
    {
        var store = NewStore();
        var household = NewId();
        var other = NewId();
        var (late, soon, expired, theirs) = (NewCode(), NewCode(), NewCode(), NewCode());
        await store.AddInviteAsync(new Invite(late, household, Start.AddDays(7)), Ct);
        await store.AddInviteAsync(new Invite(soon, household, Start.AddDays(2)), Ct);
        await store.AddInviteAsync(new Invite(expired, household, Start.AddHours(1)), Ct);
        await store.AddInviteAsync(new Invite(theirs, other, Start.AddDays(1)), Ct);

        (await store.ListInvitesAsync(household, Start.AddHours(2), Ct)).Select(i => i.Code).ShouldBe([soon, late]);

        (await store.RevokeInviteAsync(other, soon, Ct)).ShouldBeFalse(); // not theirs to revoke
        (await store.RevokeInviteAsync(household, soon, Ct)).ShouldBeTrue();
        (await store.RevokeInviteAsync(household, soon, Ct)).ShouldBeFalse();
        (await store.FindInviteAsync(soon, Start, Ct)).ShouldBeNull();
        (await store.ListInvitesAsync(household, Start.AddHours(2), Ct)).Select(i => i.Code).ShouldBe([late]);
    }

    [Fact]
    public async Task Deleting_a_household_takes_its_invites_along()
    {
        var store = NewStore();
        var gone = NewId();
        var kept = NewId();
        await store.SaveAsync(new Household(gone, "Gone", "alice"), Ct);
        await store.SaveAsync(new Household(kept, "Kept", "carol"), Ct);
        var (goneCode, keptCode) = (NewCode(), NewCode());
        await store.AddInviteAsync(new Invite(goneCode, gone, Start.AddDays(7)), Ct);
        await store.AddInviteAsync(new Invite(keptCode, kept, Start.AddDays(7)), Ct);

        await store.DeleteAsync(gone, Ct);

        (await store.FindAsync(gone, Ct)).ShouldBeNull();
        (await store.FindInviteAsync(goneCode, Start, Ct)).ShouldBeNull();
        (await store.FindAsync(kept, Ct)).ShouldNotBeNull();
        (await store.FindInviteAsync(keptCode, Start, Ct)).ShouldNotBeNull();
    }
}

public sealed class InMemoryHouseholdStoreTests : HouseholdStoreContract
{
    protected override IHouseholdStore NewStore(IEnumerable<Household>? initial = null) => new InMemoryHouseholdStore(initial);
}

/// <summary>The Cosmos DB household store against a real Cosmos DB; skips without <c>COSMOS_CONNECTION_STRING</c>.</summary>
public sealed class CosmosHouseholdStoreTests : HouseholdStoreContract, IAsyncDisposable
{
    private readonly CosmosTestDatabase database = new();

    protected override IHouseholdStore NewStore(IEnumerable<Household>? initial = null) => new CosmosHouseholdStore(database.New(), Time, initial);

    public ValueTask DisposeAsync() => database.DisposeAsync();

    [Fact]
    public async Task Households_and_invites_hold_for_a_new_instance()
    {
        var id = NewId();
        var code = NewCode();
        await NewStore().SaveAsync(new Household(id, "Home", "alice"), Ct);
        await NewStore().AddInviteAsync(new Invite(code, id, Start.AddDays(7), MaxUses: 3), Ct);
        await NewStore().TakeInviteAsync(code, Start, Ct);

        (await NewStore().FindAsync(id, Ct))!.Name.ShouldBe("Home");
        (await NewStore().FindInviteAsync(code, Start, Ct))!.Uses.ShouldBe(1);
    }

    [Fact]
    public async Task A_renamed_starting_household_is_not_reset_by_a_restart()
    {
        var id = NewId();
        var seed = new[] { new Household(id, "Alice and Bob", "alice") };
        await NewStore(seed).SaveAsync(new Household(id, "Renamed", "alice"), Ct);

        (await NewStore(seed).FindAsync(id, Ct))!.Name.ShouldBe("Renamed");
    }

    [Fact]
    public async Task Two_people_racing_for_the_last_use_of_an_invite_let_only_one_in()
    {
        var code = NewCode();
        await NewStore().AddInviteAsync(new Invite(code, NewId(), Start.AddDays(7)), Ct);

        var results = await Task.WhenAll(NewStore().TakeInviteAsync(code, Start, Ct), NewStore().TakeInviteAsync(code, Start, Ct));

        results.Count(r => r is not null).ShouldBe(1);
    }

    [Fact]
    public async Task An_invite_expires_in_cosmos_after_its_end()
    {
        var cosmos = database.New();
        var store = new CosmosHouseholdStore(cosmos, Time);
        var household = NewId();
        var code = NewCode();
        await store.AddInviteAsync(new Invite(code, household, Start.AddDays(7)), Ct);

        var container = await cosmos.ContainerAsync(CosmosHouseholdStore.Container, Ct);
        var document = await container.ReadItemAsync<JsonObject>("invite-" + code, new PartitionKey(household), cancellationToken: Ct);
        document.Resource["ttl"]!.GetValue<int>().ShouldBe((int)TimeSpan.FromDays(7).TotalSeconds + 60);
    }
}
