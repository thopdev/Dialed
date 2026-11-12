using Dialed.Domain.Inventory;
using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class BeanJourneyTests
{
    private static readonly DateOnly Today = new(2026, 11, 11);

    private static readonly Bean Medium = new() { Id = "bean", Name = "Guji", RoastLevel = RoastLevel.Medium };

    private static Brew BrewOn(string bag, DateOnly day) => new()
    {
        BagId = bag,
        MethodCode = "v60",
        DoseG = 18m,
        BrewedAt = new DateTimeOffset(day.ToDateTime(new TimeOnly(8, 0)), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(new TimeOnly(8, 0)))),
    };

    private static IReadOnlyList<JourneyEvent> Build(IEnumerable<Bag> bags, IEnumerable<Brew>? brews = null, Bean? bean = null) =>
        BeanJourney.Build(bean ?? Medium, bags, brews ?? [], InventorySettings.Default, Today);

    [Fact]
    public void A_bag_gives_purchase_roast_the_peak_window_and_the_first_brew_in_date_order()
    {
        var roast = Today.AddDays(-10); // medium: rest 7 days, peak until day 21
        var bag = new Bag { Id = "b1", BeanId = "bean", WeightG = 250m, PurchasedOn = Today.AddDays(-8), RoastDate = roast };

        var journey = Build([bag], [BrewOn("b1", Today.AddDays(-2)), BrewOn("b1", Today.AddDays(-1))]);

        journey.Select(e => (e.Kind, e.Date)).ShouldBe(
        [
            (JourneyKind.Roasted, roast),
            (JourneyKind.Purchased, Today.AddDays(-8)),
            (JourneyKind.PeakStarts, roast.AddDays(7)),
            (JourneyKind.FirstBrew, Today.AddDays(-2)),
            (JourneyKind.PeakEnds, roast.AddDays(21)),
        ]);
        journey.Single(e => e.Kind == JourneyKind.PeakEnds).Upcoming.ShouldBeTrue();
        journey.Single(e => e.Kind == JourneyKind.PeakStarts).Upcoming.ShouldBeFalse();
    }

    [Fact]
    public void A_finished_bag_ends_with_its_last_brew()
    {
        var bag = new Bag { Id = "b1", BeanId = "bean", WeightG = 250m, Status = BagStatus.Finished };

        var journey = Build([bag], [BrewOn("b1", Today.AddDays(-9)), BrewOn("b1", Today.AddDays(-3)), BrewOn("b1", Today.AddDays(-5))]);

        journey.Select(e => (e.Kind, e.Date)).ShouldBe([(JourneyKind.FirstBrew, Today.AddDays(-9)), (JourneyKind.Finished, Today.AddDays(-3))]);
    }

    [Fact]
    public void The_bags_own_rest_days_and_the_roast_level_decide_where_the_peak_starts()
    {
        var roast = Today.AddDays(-1);
        var dark = new Bean { Id = "bean", Name = "Dark", RoastLevel = RoastLevel.Dark }; // rest 5 days
        var bags = new[] { new Bag { Id = "b1", BeanId = "bean", WeightG = 250m, RoastDate = roast }, new Bag { Id = "b2", BeanId = "bean", WeightG = 250m, RoastDate = roast, RestDaysOverride = 12 } };

        var starts = Build(bags, bean: dark).Where(e => e.Kind == JourneyKind.PeakStarts).ToDictionary(e => e.BagId, e => e.Date);

        starts["b1"].ShouldBe(roast.AddDays(5));
        starts["b2"].ShouldBe(roast.AddDays(12));
    }

    [Fact]
    public void Other_beans_deleted_bags_and_missing_dates_leave_nothing_to_show()
    {
        Bag[] bags =
        [
            new() { Id = "b1", BeanId = "other", WeightG = 250m, RoastDate = Today },
            new() { Id = "b2", BeanId = "bean", WeightG = 250m, RoastDate = Today, Deleted = true },
            new() { Id = "b3", BeanId = "bean", WeightG = 250m },
        ];

        Build(bags).ShouldBeEmpty();
    }
}
