using Dialed.Domain.Model;
using Dialed.Domain.Statistics;

namespace Dialed.Domain.Tests;

public sealed class MilestonesTests
{
    private static readonly DateOnly Today = new(2026, 11, 11);

    private static Brew B(DateOnly day, string bag = "bag") => new()
    {
        BagId = bag,
        MethodCode = "v60",
        DoseG = 18m,
        BrewedAt = new DateTimeOffset(day.ToDateTime(new TimeOnly(8, 0)), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(new TimeOnly(8, 0)))),
    };

    private static MilestoneSummary Calc(IEnumerable<Brew> brews, IEnumerable<Bag>? bags = null, IEnumerable<Bean>? beans = null) =>
        MilestoneSummary.Calculate(brews, bags ?? [], beans ?? [], Today);

    [Fact]
    public void A_run_of_days_is_a_streak_and_it_stays_alive_until_the_day_after()
    {
        var yesterdayRun = Calc([B(Today.AddDays(-3)), B(Today.AddDays(-2)), B(Today.AddDays(-1))]);
        yesterdayRun.CurrentStreak.ShouldBe(3);

        var broken = Calc([B(Today.AddDays(-4)), B(Today.AddDays(-3)), B(Today.AddDays(-2))]);
        broken.CurrentStreak.ShouldBe(0);
        broken.LongestStreak.ShouldBe(3);
    }

    [Fact]
    public void Two_brews_on_one_day_count_as_one_day_and_the_longest_run_is_kept()
    {
        var summary = Calc([B(Today), B(Today), B(Today.AddDays(-1)), B(Today.AddDays(-10)), B(Today.AddDays(-11)), B(Today.AddDays(-12)), B(Today.AddDays(-13))]);

        summary.CurrentStreak.ShouldBe(2);
        summary.LongestStreak.ShouldBe(4);
    }

    [Fact]
    public void Counts_brews_origins_and_roasters_of_the_beans_you_brewed_once_each()
    {
        Bean[] beans =
        [
            new() { Id = "1", Name = "A", RoasterId = "r1", Origin = new BeanOrigin("Ethiopia") },
            new() { Id = "2", Name = "B", RoasterId = "r1", Origin = new BeanOrigin("Ethiopië") },
            new() { Id = "3", Name = "C", RoasterId = "r2", Origin = new BeanOrigin("Kenya") },
            new() { Id = "4", Name = "Never brewed", RoasterId = "r3", Origin = new BeanOrigin("Brazil") },
        ];
        Bag[] bags = [new() { Id = "b1", BeanId = "1", WeightG = 1m }, new() { Id = "b2", BeanId = "2", WeightG = 1m }, new() { Id = "b3", BeanId = "3", WeightG = 1m }, new() { Id = "b4", BeanId = "4", WeightG = 1m }];

        var summary = Calc([B(Today, "b1"), B(Today, "b1"), B(Today, "b2"), B(Today, "b3")], bags, beans);

        (summary.Brews, summary.Origins, summary.Roasters).ShouldBe((4, 2, 2));
    }

    [Fact]
    public void Shows_the_highest_step_reached_per_track_and_the_closest_next_one()
    {
        var brews = Enumerable.Range(0, 52).Select(i => B(Today.AddDays(-i * 2))).ToList(); // 52 brews, never two days in a row

        var summary = Calc(brews);

        summary.Earned.Select(m => (m.Kind, m.Threshold)).ShouldBe([(MilestoneKind.Brews, 50)]);
        // The next step is the one with the smallest share still to go: 48 of 100 brews, not 2 of 3 days in a row.
        (summary.Next!.Kind, summary.Next.Threshold).ShouldBe((MilestoneKind.Brews, 100));
    }

    [Fact]
    public void Nothing_brewed_earns_nothing_and_the_next_step_is_the_first_one()
    {
        var summary = Calc([]);

        summary.Earned.ShouldBeEmpty();
        summary.Next.ShouldNotBeNull();
        summary.CurrentStreak.ShouldBe(0);
    }
}
