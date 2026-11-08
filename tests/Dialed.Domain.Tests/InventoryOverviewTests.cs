using Dialed.Domain.Inventory;
using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class InventoryOverviewTests
{
    private static readonly DateOnly Today = new(2026, 3, 31);

    private static Bag Bag(string id = "bag", decimal weight = 250m, BagStatus status = BagStatus.Open, int roastedDaysAgo = 10) => new()
    {
        Id = id,
        BeanId = "bean",
        WeightG = weight,
        Status = status,
        RoastDate = Today.AddDays(-roastedDaysAgo),
    };

    private static Brew BrewOf(string bagId, decimal dose) => new() { BagId = bagId, MethodCode = "v60", DoseG = dose };

    [Theory]
    [InlineData(0, Freshness.Resting)]
    [InlineData(6, Freshness.Resting)]
    [InlineData(7, Freshness.Peak)]
    [InlineData(21, Freshness.Peak)]
    [InlineData(22, Freshness.Past)]
    public void Classify_uses_the_rest_window(int age, Freshness expected) =>
        FreshnessRules.Classify(age, RestWindows.Default.Medium).ShouldBe(expected);

    [Fact]
    public void Classify_is_unknown_without_a_roast_date() =>
        FreshnessRules.Classify(null, RestWindows.Default.Medium).ShouldBe(Freshness.Unknown);

    [Fact]
    public void A_bags_own_rest_days_override_the_window() =>
        FreshnessRules.Classify(10, RestWindows.Default.Medium, restDaysOverride: 14).ShouldBe(Freshness.Resting);

    [Fact]
    public void Peak_never_ends_before_the_rest_does() =>
        FreshnessRules.Classify(20, new RestWindow(10, 5)).ShouldBe(Freshness.Past);

    [Theory]
    [InlineData(RoastLevel.Light, 10)]
    [InlineData(RoastLevel.MediumLight, 10)]
    [InlineData(RoastLevel.Medium, 7)]
    [InlineData(null, 7)]
    [InlineData(RoastLevel.MediumDark, 5)]
    [InlineData(RoastLevel.Dark, 5)]
    public void Windows_follow_the_roast_level(RoastLevel? level, int restDays) =>
        RestWindows.Default.For(level).RestDays.ShouldBe(restDays);

    [Fact]
    public void Remaining_and_cups_use_the_bags_average_dose()
    {
        var stock = InventoryOverview.Build([Bag()], [new Bean { Id = "bean", Name = "Gesha" }], [BrewOf("bag", 20m), BrewOf("bag", 20m)], Today, InventorySettings.Default).Single();

        stock.RemainingG.ShouldBe(210m);
        stock.Cups.ShouldBe(10);
        stock.BeanName.ShouldBe("Gesha");
        stock.AgeDays.ShouldBe(10);
        stock.Freshness.ShouldBe(Freshness.Peak);
    }

    [Fact]
    public void A_bag_without_brews_uses_the_household_average_then_a_fallback()
    {
        var bags = new[] { Bag("a"), Bag("b") };
        var withHousehold = InventoryOverview.Build(bags, [], [BrewOf("a", 25m)], Today, InventorySettings.Default);
        withHousehold.Single(s => s.Bag.Id == "b").Cups.ShouldBe(10);

        var none = InventoryOverview.Build([Bag("b")], [], [], Today, InventorySettings.Default).Single();
        none.Cups.ShouldBe(13);
    }

    [Fact]
    public void Only_open_bags_below_the_threshold_are_low()
    {
        var settings = InventorySettings.Default with { LowStockG = 100m };
        var bags = new[]
        {
            Bag("low", weight: 80m),
            Bag("fine", weight: 250m),
            Bag("sealed", weight: 80m, status: BagStatus.Sealed),
        };

        var stock = InventoryOverview.Build(bags, [], [], Today, settings);

        stock.Where(s => s.IsLow).Select(s => s.Bag.Id).ShouldBe(["low"]);
        InventoryOverview.LowStockCount(stock).ShouldBe(1);
    }

    [Fact]
    public void Deleted_brews_and_bags_are_ignored_and_remaining_never_goes_below_zero()
    {
        var gone = Bag("gone") with { Deleted = true };
        var empty = Bag("empty", weight: 10m);
        var stock = InventoryOverview.Build([gone, empty], [], [BrewOf("empty", 18m), BrewOf("empty", 18m) with { Deleted = true }], Today, InventorySettings.Default);

        stock.Select(s => s.Bag.Id).ShouldBe(["empty"]);
        stock[0].RemainingG.ShouldBe(0m);
        stock[0].Cups.ShouldBe(0);
    }

    [Fact]
    public void The_roast_level_of_the_bean_picks_the_window()
    {
        var dark = new Bean { Id = "bean", Name = "Dark", RoastLevel = RoastLevel.Dark };
        var stock = InventoryOverview.Build([Bag(roastedDaysAgo: 20)], [dark], [], Today, InventorySettings.Default).Single();

        stock.Freshness.ShouldBe(Freshness.Past);
    }
}
