using Bunit;
using Dialed.Domain.Model;
using Dialed.Domain.Statistics;
using Dialed.UI.Components;

namespace Dialed.UI.Tests;

public sealed class MilestoneBadgesTests : UiTestContext
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
    public void Shows_what_you_reached_the_current_streak_and_the_next_step()
    {
        var brews = Enumerable.Range(0, 12).Select(i => B(Today.AddDays(-i))).ToList(); // 12 days in a row, up to today
        var summary = MilestoneSummary.Calculate(brews, [], [], Today);

        var page = Render<MilestoneBadges>(p => p.Add(c => c.Summary, summary));

        page.FindAll("#milestones-earned .dialed-milestone-badge").Select(b => b.TextContent).ShouldBe(["10 brews", "7-day streak"]);
        page.Find("#milestones-streak").TextContent.ShouldBe("Current streak: 12 days");
        page.Find("#milestones-next").TextContent.ShouldContain("Next:");
    }

    [Fact]
    public void Says_what_to_do_when_nothing_is_earned_yet_and_hides_a_streak_of_one()
    {
        var summary = MilestoneSummary.Calculate([B(Today)], [], [], Today);

        var page = Render<MilestoneBadges>(p => p.Add(c => c.Summary, summary));

        page.Find("#milestones-none").TextContent.ShouldContain("first milestone");
        page.FindAll("#milestones-streak").ShouldBeEmpty();
    }
}
