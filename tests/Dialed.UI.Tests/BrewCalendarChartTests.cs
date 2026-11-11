using Bunit;
using Dialed.Domain.Model;
using Dialed.Domain.Statistics;
using Dialed.UI.Components;

namespace Dialed.UI.Tests;

public sealed class BrewCalendarChartTests : UiTestContext
{
    private static readonly DateOnly Today = new(2026, 11, 11);

    private static Brew B(DateOnly day) => new()
    {
        BagId = "bag",
        MethodCode = "v60",
        DoseG = 18m,
        BrewedAt = new DateTimeOffset(day.ToDateTime(new TimeOnly(8, 0)), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(new TimeOnly(8, 0)))),
    };

    [Fact]
    public void Draws_a_square_per_past_day_marks_roast_days_and_describes_the_busiest_day()
    {
        var calendar = BrewCalendar.Build([B(Today), B(Today), B(Today.AddDays(-1))], [new Bag { BeanId = "b", WeightG = 250m, RoastDate = Today.AddDays(-3) }], Today, weeks: 2);

        var chart = Render<BrewCalendarChart>(p => p.Add(c => c.Calendar, calendar));

        chart.FindAll("svg[role=img] rect.dialed-calendar-day").Count.ShouldBe(7 + 3); // last week fully, this week Monday to Wednesday
        chart.FindAll("svg[role=img] circle.dialed-calendar-roast").Count.ShouldBe(1);
        chart.Find("svg[role=img]").GetAttribute("aria-label")!.ShouldContain("3 brews in the last 2 weeks");
        chart.Markup.ShouldContain("2 brews");
        chart.Find(".dialed-calendar-l4").ShouldNotBeNull();
    }

    [Fact]
    public void An_empty_calendar_says_there_were_no_brews()
    {
        var chart = Render<BrewCalendarChart>(p => p.Add(c => c.Calendar, BrewCalendar.Build([], [], Today, weeks: 3)));

        chart.Find("svg[role=img]").GetAttribute("aria-label").ShouldBe("No brews in the last 3 weeks");
    }
}
