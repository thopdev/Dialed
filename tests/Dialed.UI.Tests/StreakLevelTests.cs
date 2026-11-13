using Bunit;
using Dialed.Domain.Model;
using Dialed.Domain.Statistics;
using Dialed.UI.Components;

namespace Dialed.UI.Tests;

public sealed class StreakLevelTests : UiTestContext
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
    public void Shows_the_days_in_a_row_and_the_level_with_what_is_left_to_the_next()
    {
        var brews = Enumerable.Range(0, 12).Select(i => B(Today.AddDays(-i))).ToList();
        var page = Render<StreakLevel>(p => p.Add(c => c.Summary, MilestoneSummary.Calculate(brews, [], [], Today)));

        page.Find("#stats-streak-level-days").TextContent.ShouldBe("12");
        page.Find("#stats-streak-level-level").TextContent.ShouldBe("Home brewer");
        page.Find("#stats-streak-level-detail").TextContent.ShouldBe("38 brews to go for Dialed in");
    }

    [Fact]
    public void A_new_user_starts_as_a_beginner_with_no_streak()
    {
        var page = Render<StreakLevel>(p => p.Add(c => c.Summary, MilestoneSummary.Calculate([], [], [], Today)));

        page.Find("#stats-streak-level-days").TextContent.ShouldBe("0");
        page.Find("#stats-streak-level-level").TextContent.ShouldBe("Beginner");
    }

    [Fact]
    public void The_level_meter_is_announced()
    {
        var page = Render<StreakLevel>(p => p.Add(c => c.Summary, MilestoneSummary.Calculate([B(Today)], [], [], Today)));

        page.Find("[role=meter]").GetAttribute("aria-label").ShouldBe("Progress to the next level");
    }
}
