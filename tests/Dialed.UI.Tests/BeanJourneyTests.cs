using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.Inventory;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class BeanJourneyTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    [Fact]
    public void Shows_each_step_with_its_date_and_bag_and_mutes_the_ones_still_ahead()
    {
        JourneyEvent[] events =
        [
            new(new DateOnly(2026, 10, 1), JourneyKind.Roasted, "b1", 250m, Upcoming: false),
            new(new DateOnly(2026, 12, 1), JourneyKind.PeakEnds, "b1", 250m, Upcoming: true),
        ];

        var list = Render<BeanJourneyList>(p => p.Add(c => c.Events, events));

        var steps = list.FindAll(".dialed-journey-step");
        steps.Count.ShouldBe(2);
        steps[0].TextContent.ShouldContain("Roasted");
        steps[0].TextContent.ShouldContain("1 Oct 2026");
        list.Find(".dialed-journey-bag h3").TextContent.ShouldBe("250 g bag");
        steps[0].ClassList.ShouldNotContain("upcoming");
        steps[1].TextContent.ShouldContain("Peak window ends");
        steps[1].TextContent.ShouldContain("Upcoming");
        steps[1].ClassList.ShouldContain("upcoming");
        list.Find("time").GetAttribute("datetime").ShouldBe("2026-10-01");
    }

    [Fact]
    public void Each_bag_gets_its_own_group_and_the_earliest_bag_comes_first()
    {
        JourneyEvent[] events =
        [
            new(new DateOnly(2026, 11, 1), JourneyKind.Roasted, "new", 500m, Upcoming: false),
            new(new DateOnly(2026, 10, 1), JourneyKind.Roasted, "old", 250m, Upcoming: false),
            new(new DateOnly(2026, 10, 20), JourneyKind.Finished, "old", 250m, Upcoming: false),
        ];

        var list = Render<BeanJourneyList>(p => p.Add(c => c.Events, events));

        list.FindAll(".dialed-journey-bag h3").Select(h => h.TextContent).ShouldBe(["250 g bag", "500 g bag"]);
        list.FindAll("[data-bag=old] .dialed-journey-step").Select(s => s.GetAttribute("data-kind")).ShouldBe(["Roasted", "Finished"]);
    }

    private async Task<Bean> BeanWith(Bag? bag)
    {
        var bean = await Services.GetRequiredService<IRepository<Bean>>().SaveAsync(new Bean { Name = "Guji", RoastLevel = RoastLevel.Medium }, Ct);
        if (bag is not null)
        {
            await Services.GetRequiredService<IRepository<Bag>>().SaveAsync(bag with { BeanId = bean.Id }, Ct);
        }

        return bean;
    }

    [Fact]
    public async Task The_bean_page_shows_the_journey_once_there_are_two_steps()
    {
        var bean = await BeanWith(new Bag { BeanId = "x", WeightG = 250m, PurchasedOn = Today.AddDays(-3), RoastDate = Today.AddDays(-5) });

        var page = Render<Beans>(p => p.Add(b => b.Id, bean.Id));

        page.WaitForAssertion(() => page.Find("#bean-journey"));
        page.FindAll("#bean-journey .dialed-journey-step").Select(s => s.GetAttribute("data-kind")).ShouldBe(["Roasted", "Purchased", "PeakStarts", "PeakEnds"]);
    }

    [Fact]
    public async Task A_bean_without_dates_has_no_journey()
    {
        var bean = await BeanWith(new Bag { BeanId = "x", WeightG = 250m });

        var page = Render<Beans>(p => p.Add(b => b.Id, bean.Id));

        page.WaitForAssertion(() => page.Find("#bean-hero"));
        page.FindAll("#bean-journey").ShouldBeEmpty();
    }
}
