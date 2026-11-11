using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.Model;
using Dialed.Domain.Statistics;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class DialInChartTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static Brew B(string bagId, int day, decimal grind, int? rating = null, int? time = null, string method = "v60") => new()
    {
        BagId = bagId,
        MethodCode = method,
        DoseG = 18m,
        GrindSetting = grind,
        Rating = rating,
        TimeS = time,
        BrewedAt = new DateTimeOffset(2026, 11, day, 8, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void Draws_a_dot_per_brew_rings_the_best_and_says_where_you_settled()
    {
        var series = DialInTrace.Build([B("x", 1, 20, 2), B("x", 2, 18, 3), B("x", 3, 16, 5, 175), B("x", 4, 16, 4), B("x", 5, 16, 4)]).Single();

        var chart = Render<DialInChart>(p => p.Add(c => c.Series, series));

        chart.FindAll("circle.dialed-dialin-dot").Count.ShouldBe(5);
        chart.FindAll("circle.dialed-dialin-best").Count.ShouldBe(1);
        chart.Find("svg").GetAttribute("aria-label").ShouldBe("Grind setting over 5 brews, from 20 to 16");
        chart.Find(".dialed-dialin-best-line").TextContent.ShouldContain("grind 16, 5 stars");
        chart.Markup.ShouldContain("2:55"); // brew time in the dot's tooltip
        chart.Markup.ShouldContain("dialed in");
    }

    [Fact]
    public void A_trace_without_ratings_has_no_best_line_and_hollow_dots()
    {
        var series = DialInTrace.Build([B("x", 1, 20), B("x", 2, 18), B("x", 3, 17)]).Single();

        var chart = Render<DialInChart>(p => p.Add(c => c.Series, series));

        chart.FindAll(".dialed-dialin-best-line").ShouldBeEmpty();
        chart.FindAll("circle.dialed-dialin-unrated").Count.ShouldBe(3);
    }

    private async Task<Bean> BeanWithBrews(int count)
    {
        var bean = await Services.GetRequiredService<IRepository<Bean>>().SaveAsync(new Bean { Name = "Guji" }, Ct);
        var bag = await Services.GetRequiredService<IRepository<Bag>>().SaveAsync(new Bag { BeanId = bean.Id, WeightG = 250m }, Ct);
        var brews = Services.GetRequiredService<IRepository<Brew>>();
        for (var day = 1; day <= count; day++)
        {
            await brews.SaveAsync(B(bag.Id, day, 20 - day, 4), Ct);
        }

        return bean;
    }

    [Fact]
    public async Task The_bean_page_shows_the_trace_once_there_are_enough_brews()
    {
        var bean = await BeanWithBrews(3);

        var page = Render<Beans>(p => p.Add(b => b.Id, bean.Id));

        page.WaitForAssertion(() => page.Find("#bean-dialin-v60 svg"));
    }

    [Fact]
    public async Task Two_brews_are_not_a_trace_yet()
    {
        var bean = await BeanWithBrews(2);

        var page = Render<Beans>(p => p.Add(b => b.Id, bean.Id));

        page.WaitForAssertion(() => page.Find("#bean-brews"));
        page.FindAll("#bean-dialin-v60").ShouldBeEmpty();
    }
}
