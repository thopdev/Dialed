using Dialed.Domain.Model;

namespace Dialed.Domain.Statistics;

/// <summary>One day on the brewing calendar: how many brews, and whether a bag was roasted that day.</summary>
public sealed record CalendarDay(DateOnly Date, int Brews, bool Roasted, bool InFuture);

/// <summary>A week of the calendar, Monday first, always seven days.</summary>
public sealed record CalendarWeek(DateOnly Monday, IReadOnlyList<CalendarDay> Days);

/// <summary>The last weeks as a grid of days, like a contribution calendar: brews per day, with the roast days of your bags marked so you can see freshness against use.</summary>
public sealed record BrewCalendar(IReadOnlyList<CalendarWeek> Weeks, int TotalBrews, CalendarDay? BusiestDay)
{
    public const int DefaultWeeks = 15;

    /// <summary>How strongly to colour a day, 0 (no brew) to 4, relative to the busiest day shown.</summary>
    public int Level(CalendarDay day)
    {
        ArgumentNullException.ThrowIfNull(day);
        var max = BusiestDay?.Brews ?? 0;
        return day.Brews == 0 || max == 0 ? 0 : Math.Clamp((int)Math.Ceiling(4.0 * day.Brews / max), 1, 4);
    }

    public static BrewCalendar Build(IEnumerable<Brew> brews, IEnumerable<Bag> bags, DateOnly today, int weeks = DefaultWeeks)
    {
        ArgumentNullException.ThrowIfNull(brews);
        ArgumentNullException.ThrowIfNull(bags);
        ArgumentOutOfRangeException.ThrowIfLessThan(weeks, 1);

        var firstMonday = CoffeeStatistics.MondayOf(today).AddDays(-7 * (weeks - 1));
        var perDay = brews.Where(b => !b.Deleted)
            .GroupBy(b => DateOnly.FromDateTime(b.BrewedAt.LocalDateTime))
            .ToDictionary(g => g.Key, g => g.Count());
        var roastDays = bags.Where(b => !b.Deleted && b.RoastDate is not null).Select(b => b.RoastDate!.Value).ToHashSet();

        var grid = Enumerable.Range(0, weeks).Select(w =>
        {
            var monday = firstMonday.AddDays(7 * w);
            var days = Enumerable.Range(0, 7).Select(d =>
            {
                var date = monday.AddDays(d);
                return new CalendarDay(date, perDay.GetValueOrDefault(date), roastDays.Contains(date), date > today);
            }).ToList();
            return new CalendarWeek(monday, days);
        }).ToList();

        var shown = grid.SelectMany(w => w.Days).Where(d => !d.InFuture).ToList();
        var busiest = shown.Where(d => d.Brews > 0).OrderByDescending(d => d.Brews).ThenByDescending(d => d.Date).FirstOrDefault();
        return new BrewCalendar(grid, shown.Sum(d => d.Brews), busiest);
    }
}
