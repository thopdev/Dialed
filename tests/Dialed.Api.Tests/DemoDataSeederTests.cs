using Dialed.Api.Seeding;
using Dialed.Api.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dialed.Api.Tests;

/// <summary>The development demo data (#228): a full household for Alice and Bob, valid records, safe to run twice.</summary>
public sealed class DemoDataSeederTests
{
    private const string Household = "dev-household";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly InMemorySyncStore store = new(TimeProvider.System);

    private Task SeedAsync() => new DemoDataSeeder(store, TimeProvider.System, NullLogger<DemoDataSeeder>.Instance).SeedAsync(Ct);

    private async Task<IReadOnlyList<SyncRow>> Rows(string table) =>
        (await store.PullAsync(Household, "dev-alice", table, null, null, 500, Ct)).Rows;

    [Fact]
    public async Task Fills_every_table_the_screens_use()
    {
        await SeedAsync();

        (await Rows("vendors")).Count.ShouldBe(4);
        (await Rows("beans")).Count.ShouldBe(5);
        (await Rows("bags")).Count.ShouldBe(5);
        (await Rows("equipment")).Count.ShouldBe(5);
        (await Rows("recipes")).Count.ShouldBe(4);
        (await Rows("brews")).Count.ShouldBeGreaterThan(20);
    }

    [Fact]
    public async Task Has_bags_in_every_state_and_a_brew_by_Bob()
    {
        await SeedAsync();

        (await Rows("bags")).Select(r => r.Entity["status"]!.GetValue<int>()).Distinct().Order().ShouldBe([0, 1, 2, 3]);
        (await Rows("brews")).Count(r => r.Entity["createdBy"]!.GetValue<string>() == "dev-bob").ShouldBe(1);
        (await Rows("recipes")).Count(r => r.Entity["isSuperseded"]!.GetValue<bool>()).ShouldBe(1);
    }

    [Fact]
    public async Task Every_record_passes_the_sync_validation()
    {
        await SeedAsync();

        foreach (var table in SyncEndpoints.Tables)
        {
            foreach (var row in await Rows(table))
            {
                SyncPayloadValidator.Validate(row.Id, row.Entity).ShouldBeEmpty($"{table}/{row.Id}");
            }
        }
    }

    [Fact]
    public async Task Running_twice_changes_nothing()
    {
        await SeedAsync();
        var versions = (await Rows("brews")).Select(r => (r.Id, r.Version)).ToList();

        await SeedAsync();

        (await Rows("brews")).Select(r => (r.Id, r.Version)).ShouldBe(versions);
    }

    [Fact]
    public async Task Only_Alice_and_Bobs_household_gets_data()
    {
        await SeedAsync();

        (await store.PullAsync("dev-household-carol", "dev-carol", "beans", null, null, 500, Ct)).Rows.ShouldBeEmpty();
    }
}
