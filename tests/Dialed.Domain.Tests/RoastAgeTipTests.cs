using Dialed.Domain.Inventory;
using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class RoastAgeTipTests
{
    private static readonly DateOnly Today = new(2026, 11, 20);

    private static Bag BagRoasted(string id, DateOnly? roasted, string bean = "bean") => new() { Id = id, BeanId = bean, WeightG = 250m, RoastDate = roasted };

    private static Brew On(Bag bag, DateOnly day, int? rating) =>
        new() { BagId = bag.Id, MethodCode = "v60", DoseG = 15m, Rating = rating, BrewedAt = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(new TimeOnly(12, 0)))) };

    private static (Bag Bag, Brew[] Brews) Setup()
    {
        var bag = BagRoasted("bag", new DateOnly(2026, 11, 1));
        return (bag, [On(bag, new DateOnly(2026, 11, 9), 5), On(bag, new DateOnly(2026, 11, 12), 4), On(bag, new DateOnly(2026, 11, 15), 5), On(bag, new DateOnly(2026, 11, 3), 2)]);
    }

    [Fact]
    public void The_range_and_median_come_from_the_brews_rated_four_or_more()
    {
        var (bag, brews) = Setup();

        var tip = RoastAgeTip.Build("bean", [bag], brews, bag, new DateOnly(2026, 11, 12));

        tip.ShouldNotBeNull();
        (tip.FromDays, tip.ToDays, tip.MedianDays, tip.Brews).ShouldBe((8, 14, 11, 3));
        (tip.Position, tip.DaysAway).ShouldBe((RoastAgePosition.Inside, 0));
    }

    [Fact]
    public void A_bag_that_is_too_young_or_too_old_says_by_how_many_days()
    {
        var (bag, brews) = Setup();

        var young = RoastAgeTip.Build("bean", [bag], brews, bag, new DateOnly(2026, 11, 6))!;
        var old = RoastAgeTip.Build("bean", [bag], brews, bag, new DateOnly(2026, 12, 1))!;

        (young.Position, young.DaysAway).ShouldBe((RoastAgePosition.Before, 3));
        (old.Position, old.DaysAway).ShouldBe((RoastAgePosition.After, 16));
    }

    [Fact]
    public void Earlier_bags_of_the_bean_count_and_the_current_bag_is_judged_by_its_own_age()
    {
        var earlier = BagRoasted("old", new DateOnly(2026, 9, 1));
        var current = BagRoasted("new", new DateOnly(2026, 11, 10));
        Brew[] brews = [On(earlier, new DateOnly(2026, 9, 10), 5), On(earlier, new DateOnly(2026, 9, 12), 4), On(earlier, new DateOnly(2026, 9, 14), 5)];

        var tip = RoastAgeTip.Build("bean", [earlier, current], brews, current, new DateOnly(2026, 11, 20))!;

        (tip.FromDays, tip.ToDays, tip.Position).ShouldBe((9, 13, RoastAgePosition.Inside));
    }

    [Fact]
    public void Too_little_data_gives_nothing()
    {
        var (bag, brews) = Setup();
        var undated = BagRoasted("u", null);

        RoastAgeTip.Build("bean", [bag], brews.Take(2), bag, Today).ShouldBeNull();
        RoastAgeTip.Build("bean", [bag], brews, undated, Today).ShouldBeNull();
        RoastAgeTip.Build("other", [bag], brews, bag, Today).ShouldBeNull();
        RoastAgeTip.Build("bean", [undated], [On(undated, Today, 5), On(undated, Today, 5), On(undated, Today, 5)], bag, Today).ShouldBeNull();
    }
}
