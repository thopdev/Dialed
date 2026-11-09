using Dialed.Domain.Model;
using Dialed.Domain.Statistics;

namespace Dialed.Domain.Tests;

public sealed class CoffeeStatisticsTests
{
    private static readonly DateOnly Today = new(2026, 11, 11); // a Wednesday

    private static DateTimeOffset At(int month, int day) => new(2026, month, day, 9, 0, 0, TimeSpan.Zero);

    private static Brew Brew(string bag, decimal dose, DateTimeOffset at, int? rating = null, string? grinder = null, decimal? setting = null) =>
        new() { BagId = bag, MethodCode = "v60", DoseG = dose, BrewedAt = at, Rating = rating, GrinderId = grinder, GrindSetting = setting };

    private static CoffeeStatistics Calc(IEnumerable<Brew>? brews = null, IEnumerable<Bag>? bags = null, IEnumerable<Bean>? beans = null,
        IEnumerable<Vendor>? vendors = null, IEnumerable<EquipmentItem>? gear = null) =>
        CoffeeStatistics.Calculate(brews ?? [], bags ?? [], beans ?? [], vendors ?? [], gear ?? [], Today);

    [Fact]
    public void Nothing_logged_is_empty()
    {
        var stats = Calc();

        stats.IsEmpty.ShouldBeTrue();
        stats.Weekly.Count.ShouldBe(CoffeeStatistics.Weeks);
        stats.Weekly.ShouldAllBe(w => w.Grams == 0);
    }

    [Fact]
    public void Weeks_start_on_monday_and_the_last_one_is_this_week()
    {
        CoffeeStatistics.MondayOf(new DateOnly(2026, 11, 8)).ShouldBe(new DateOnly(2026, 11, 2)); // Sunday
        CoffeeStatistics.MondayOf(new DateOnly(2026, 11, 9)).ShouldBe(new DateOnly(2026, 11, 9));

        var stats = Calc();

        stats.Weekly[^1].WeekStart.ShouldBe(new DateOnly(2026, 11, 9));
        stats.Weekly[0].WeekStart.ShouldBe(new DateOnly(2026, 11, 9).AddDays(-7 * 11));
    }

    [Fact]
    public void Consumption_adds_up_the_doses_per_week_and_ignores_deleted_brews()
    {
        var stats = Calc(brews:
        [
            Brew("b", 18m, At(11, 9)),
            Brew("b", 17m, At(11, 11)),
            Brew("b", 20m, At(11, 4)),
            Brew("b", 99m, At(11, 10)) with { Deleted = true },
        ]);

        stats.Weekly[^1].Grams.ShouldBe(35m);
        stats.Weekly[^2].Grams.ShouldBe(20m);
        stats.TotalBrews.ShouldBe(3);
    }

    [Fact]
    public void Spend_is_per_month_and_currency_by_purchase_date()
    {
        var stats = Calc(bags:
        [
            new Bag { BeanId = "x", WeightG = 250m, PriceCents = 1250, Currency = "eur", PurchasedOn = new DateOnly(2026, 11, 2) },
            new Bag { BeanId = "x", WeightG = 250m, PriceCents = 1000, PurchasedOn = new DateOnly(2026, 11, 20) },
            new Bag { BeanId = "x", WeightG = 250m, PriceCents = 900, Currency = "USD", PurchasedOn = new DateOnly(2026, 11, 3) },
            new Bag { BeanId = "x", WeightG = 250m, PriceCents = 800, PurchasedOn = new DateOnly(2026, 10, 1) },
            new Bag { BeanId = "x", WeightG = 250m, PriceCents = 700 },
            new Bag { BeanId = "x", WeightG = 250m, PurchasedOn = new DateOnly(2026, 10, 1) },
        ]);

        stats.Spend.Select(s => (s.Year, s.Month, s.Currency, s.Cents)).ShouldBe(
        [
            (2026, 11, "EUR", 2250),
            (2026, 11, "USD", 900),
            (2026, 10, "EUR", 800),
        ]);
    }

    [Fact]
    public void Favourites_rank_by_brews_then_rating_and_name_the_roaster()
    {
        var roaster = new Vendor { Name = "Friedhats" };
        var beans = new[]
        {
            new Bean { Name = "Guji", RoasterId = roaster.Id, Origin = new BeanOrigin(Country: "Ethiopia"), Process = "Washed" },
            new Bean { Name = "Huila", Origin = new BeanOrigin(Country: "Colombia"), Process = "Natural" },
        };
        var bags = beans.Select(b => new Bag { BeanId = b.Id, WeightG = 250m }).ToArray();

        var stats = Calc(
            brews:
            [
                Brew(bags[0].Id, 18m, At(11, 9), rating: 5),
                Brew(bags[0].Id, 18m, At(11, 10), rating: 4),
                Brew(bags[1].Id, 18m, At(11, 10)),
            ],
            bags: bags, beans: beans, vendors: [roaster]);

        stats.Origins.Select(o => (o.Name, o.Brews, o.AverageRating)).ShouldBe([("Ethiopia", 2, (double?)4.5), ("Colombia", 1, null)]);
        stats.Roasters.Select(r => r.Name).ShouldBe(["Friedhats"]);
        stats.Processes.Select(p => p.Name).ShouldBe(["Washed", "Natural"]);
    }

    [Fact]
    public void Grind_trends_average_the_setting_per_grinder_and_month()
    {
        var grinder = new EquipmentItem { Kind = EquipmentKind.Grinder, Brand = "Comandante", Model = "C40" };
        var other = new EquipmentItem { Kind = EquipmentKind.Grinder, Brand = "Timemore" };
        var stats = Calc(
            brews:
            [
                Brew("b", 18m, At(10, 3), grinder: grinder.Id, setting: 20m),
                Brew("b", 18m, At(10, 9), grinder: grinder.Id, setting: 22m),
                Brew("b", 18m, At(11, 9), grinder: grinder.Id, setting: 24m),
                Brew("b", 18m, At(11, 9), grinder: grinder.Id),
            ],
            gear: [grinder, other]);

        var trend = stats.GrindTrends.ShouldHaveSingleItem();
        trend.GrinderName.ShouldBe("Comandante C40");
        trend.Points.Select(p => (p.Month, p.AverageSetting, p.Brews)).ShouldBe([(10, 21m, 2), (11, 24m, 1)]);
    }
}
