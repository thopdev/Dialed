using Dialed.Domain.BrewMethods;
using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class BrewHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 8, 0, 0, TimeSpan.Zero);

    private static Brew B(string id, string bag, string method, string by, int days, int? rating = null) =>
        new() { Id = id, BagId = bag, MethodCode = method, CreatedBy = by, DoseG = 15m, BrewedAt = Now.AddDays(days), Rating = rating };

    private static readonly Bag[] Bags = [new() { Id = "bag1", BeanId = "bean1", WeightG = 250m }, new() { Id = "bag2", BeanId = "bean2", WeightG = 250m }];

    private static readonly Brew[] All =
    [
        B("1", "bag1", "v60", "me", -3, 5),
        B("2", "bag2", "espresso", "me", -1, 3),
        B("3", "bag1", "espresso", "iris", -2),
    ];

    [Fact]
    public void Newest_first() =>
        BrewHistory.Apply(All, Bags, new BrewFilter()).Select(b => b.Id).ShouldBe(["2", "3", "1"]);

    [Fact]
    public void Filters_by_bean_through_the_bag()
    {
        BrewHistory.Apply(All, Bags, new BrewFilter(BeanId: "bean1")).Select(b => b.Id).ShouldBe(["3", "1"]);
    }

    [Fact]
    public void Filters_by_method_person_and_rating()
    {
        BrewHistory.Apply(All, Bags, new BrewFilter(MethodCode: "espresso")).Select(b => b.Id).ShouldBe(["2", "3"]);
        BrewHistory.Apply(All, Bags, new BrewFilter(PersonId: "iris")).Select(b => b.Id).ShouldBe(["3"]);
        BrewHistory.Apply(All, Bags, new BrewFilter(MinRating: 4)).Select(b => b.Id).ShouldBe(["1"]);
        BrewHistory.Apply(All, Bags, new BrewFilter(MethodCode: "espresso", PersonId: "me", MinRating: 3)).Select(b => b.Id).ShouldBe(["2"]);
    }

    [Fact]
    public void Deleted_brews_are_hidden() =>
        BrewHistory.Apply([B("1", "bag1", "v60", "me", 0) with { Deleted = true }], Bags, new BrewFilter()).ShouldBeEmpty();

    [Fact]
    public void Compare_puts_the_recipe_next_to_what_was_brewed()
    {
        var recipe = new Recipe { MethodCode = "espresso", Name = "House", DoseG = 18m, YieldG = 36m, TempC = 93m, TimeS = 28 };
        var brew = B("1", "bag1", "espresso", "me", 0) with { DoseG = 18m, YieldG = 40m, TimeS = 31 };

        var rows = BrewHistory.Compare(brew, recipe);

        rows.Select(r => r.Parameter).ShouldBe([BrewParameter.Dose, BrewParameter.Yield, BrewParameter.Temperature, BrewParameter.Time]);
        rows.Single(r => r.Parameter == BrewParameter.Dose).Differs.ShouldBeFalse();
        rows.Single(r => r.Parameter == BrewParameter.Yield).ShouldBe(new ParameterDelta(BrewParameter.Yield, 36m, 40m));
        rows.Single(r => r.Parameter == BrewParameter.Yield).Differs.ShouldBeTrue();
        rows.Single(r => r.Parameter == BrewParameter.Temperature).Actual.ShouldBeNull();
    }

    [Fact]
    public void Compare_without_a_recipe_only_shows_the_actual_values()
    {
        var rows = BrewHistory.Compare(B("1", "bag1", "v60", "me", 0) with { WaterG = 240m }, null);

        rows.Select(r => r.Parameter).ShouldBe([BrewParameter.Dose, BrewParameter.Water]);
        rows.ShouldAllBe(r => r.Planned == null && !r.Differs);
    }

    [Fact]
    public void Two_brews_are_compared_value_by_value_with_what_moved()
    {
        var first = B("1", "bag1", "v60", "me", -2) with { DoseG = 15m, WaterG = 240m, GrindSetting = 20m, TimeS = 170 };
        var second = B("2", "bag1", "v60", "me", -1) with { DoseG = 16m, WaterG = 240m, GrindSetting = 18m, TempC = 94m };

        var rows = BrewHistory.CompareBrews(first, second);

        rows.Select(r => r.Parameter).ShouldBe([BrewParameter.Dose, BrewParameter.Water, BrewParameter.Grind, BrewParameter.Temperature, BrewParameter.Time]);
        rows.Single(r => r.Parameter == BrewParameter.Dose).Change.ShouldBe(1m);
        rows.Single(r => r.Parameter == BrewParameter.Grind).Change.ShouldBe(-2m);
        rows.Single(r => r.Parameter == BrewParameter.Water).Differs.ShouldBeFalse();
        rows.Single(r => r.Parameter == BrewParameter.Temperature).Change.ShouldBeNull(); // only one of them knows it
        rows.Single(r => r.Parameter == BrewParameter.Time).Change.ShouldBeNull();
    }

    [Fact]
    public void A_brew_can_be_compared_with_the_newest_other_brews_of_the_same_bean()
    {
        var brews = new[] { B("1", "bag1", "v60", "me", -3), B("2", "bag2", "v60", "me", -2), B("3", "bag1", "v60", "me", -1), B("4", "bag1", "v60", "me", -5) with { Deleted = true } };

        BrewHistory.SameBean(brews[0], brews, Bags).Select(b => b.Id).ShouldBe(["3"]); // not itself, not another bean, not a deleted one
        BrewHistory.SameBean(brews[0], brews, Bags, max: 0).ShouldBeEmpty();
        BrewHistory.SameBean(brews[0], brews, []).ShouldBeEmpty();
    }
}
