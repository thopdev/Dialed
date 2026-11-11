using Dialed.Domain.Model;
using Dialed.Domain.Statistics;

namespace Dialed.Domain.Tests;

public sealed class BrewCalendarTests
{
    // Wednesday 11 November 2026.
    private static readonly DateOnly Today = new(2026, 11, 11);

    private static Brew B(DateOnly day, bool deleted = false) => new()
    {
        BagId = "bag",
        MethodCode = "v60",
        DoseG = 18m,
        Deleted = deleted,
        BrewedAt = new DateTimeOffset(day.ToDateTime(new TimeOnly(8, 0)), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(new TimeOnly(8, 0)))),
    };

    [Fact]
    public void Is_a_grid_of_whole_weeks_Monday_first_ending_in_the_current_week()
    {
        var calendar = BrewCalendar.Build([], [], Today, weeks: 4);

        calendar.Weeks.Count.ShouldBe(4);
        calendar.Weeks.ShouldAllBe(w => w.Days.Count == 7 && w.Monday.DayOfWeek == DayOfWeek.Monday);
        calendar.Weeks[^1].Monday.ShouldBe(new DateOnly(2026, 11, 9));
        calendar.Weeks[0].Monday.ShouldBe(new DateOnly(2026, 10, 19));
        calendar.Weeks[^1].Days.Select(d => d.InFuture).ShouldBe([false, false, false, true, true, true, true]);
    }

    [Fact]
    public void Counts_brews_per_day_and_ignores_deleted_ones()
    {
        var calendar = BrewCalendar.Build([B(Today), B(Today), B(Today.AddDays(-1)), B(Today, deleted: true)], [], Today, weeks: 2);

        var days = calendar.Weeks.SelectMany(w => w.Days).ToDictionary(d => d.Date);
        days[Today].Brews.ShouldBe(2);
        days[Today.AddDays(-1)].Brews.ShouldBe(1);
        calendar.TotalBrews.ShouldBe(3);
        calendar.BusiestDay!.Date.ShouldBe(Today);
    }

    [Fact]
    public void Marks_the_roast_days_of_bags()
    {
        var roast = Today.AddDays(-5);
        var calendar = BrewCalendar.Build([], [new Bag { BeanId = "b", WeightG = 250m, RoastDate = roast }, new Bag { BeanId = "b", WeightG = 250m, RoastDate = Today.AddDays(-400) }], Today, weeks: 3);

        calendar.Weeks.SelectMany(w => w.Days).Where(d => d.Roasted).Select(d => d.Date).ShouldBe([roast]);
    }

    [Fact]
    public void Colours_a_day_relative_to_the_busiest_one()
    {
        var brews = new List<Brew> { B(Today.AddDays(-1)) };
        brews.AddRange(Enumerable.Repeat(B(Today), 4));
        var calendar = BrewCalendar.Build(brews, [], Today, weeks: 2);
        var days = calendar.Weeks.SelectMany(w => w.Days).ToDictionary(d => d.Date);

        calendar.Level(days[Today]).ShouldBe(4);
        calendar.Level(days[Today.AddDays(-1)]).ShouldBe(1);
        calendar.Level(days[Today.AddDays(-2)]).ShouldBe(0);
    }

    [Fact]
    public void Brews_outside_the_shown_weeks_do_not_count()
    {
        BrewCalendar.Build([B(Today.AddDays(-200))], [], Today, weeks: 4).TotalBrews.ShouldBe(0);
    }
}
