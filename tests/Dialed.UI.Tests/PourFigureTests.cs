using Bunit;
using Dialed.Domain.BrewMethods;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class PourFigureTests : UiTestContext
{
    [Fact]
    public void The_figure_describes_the_pattern_and_the_water_level_and_scales_the_water()
    {
        var figure = Render<PourFigure>(p => p.Add(x => x.Fraction, 0.5).Add(x => x.Pattern, PourPattern.Spiral).Add(x => x.Pouring, true).Add(x => x.Animated, true));

        var svg = figure.Find("svg");
        svg.GetAttribute("role").ShouldBe("img");
        svg.GetAttribute("aria-label").ShouldBe("Pouring In a spiral, water at 50%");
        svg.ClassList.ShouldContain("animated");
        svg.ClassList.ShouldContain("dialed-pour-spiral");
        figure.Find(".dialed-pour-water").GetAttribute("style")!.ShouldContain("scaleY(0.5)");
        figure.FindAll(".dialed-pour-dot").Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(PourPattern.Centre, 0, 1)]
    [InlineData(PourPattern.Pulse, 0, 1)]
    [InlineData(PourPattern.Spiral, 1, 0)]
    [InlineData(PourPattern.Circles, 1, 0)]
    public void Each_pattern_is_drawn_differently(PourPattern pattern, int dots, int streams)
    {
        var figure = Render<PourFigure>(p => p.Add(x => x.Fraction, 0.3).Add(x => x.Pattern, pattern).Add(x => x.Pouring, true));

        figure.FindAll(".dialed-pour-dot").Count.ShouldBe(dots);
        figure.FindAll(".dialed-pour-stream").Count.ShouldBe(streams);
    }

    [Fact]
    public void Without_pouring_there_is_only_the_brewer_and_the_level_and_the_level_is_clamped()
    {
        var figure = Render<PourFigure>(p => p.Add(x => x.Fraction, 3.0).Add(x => x.Pattern, PourPattern.Centre));

        figure.Find("svg").GetAttribute("aria-label").ShouldBe("Water at 100%");
        figure.FindAll(".dialed-pour-stream").ShouldBeEmpty();
        figure.FindAll(".dialed-pour-kettle").ShouldBeEmpty();
        figure.Find(".dialed-pour-water").GetAttribute("style")!.ShouldContain("scaleY(1)");
    }

    [Fact]
    public void A_straight_sided_brewer_is_drawn_for_the_aeropress()
    {
        var cone = Render<PourFigure>(p => p.Add(x => x.Fraction, 0.5));
        var press = Render<PourFigure>(p => p.Add(x => x.Fraction, 0.5).Add(x => x.Cylinder, true));

        cone.FindAll("path.dialed-pour-vessel").Count.ShouldBe(1);
        press.FindAll("rect.dialed-pour-vessel").Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_templates_page_lists_the_routines_with_a_figure_per_pour_and_copies_one_with_its_patterns()
    {
        var page = Render<Recipes>(p => p.Add(x => x.Id, "templates"));

        var card = page.Find("[data-routine='4:6 method (Tetsu Kasuya)']");
        card.QuerySelectorAll(".dialed-routine-figures li").Length.ShouldBe(5);
        card.QuerySelectorAll(".dialed-routine-figures li").Select(l => l.TextContent.Trim()).ShouldBe(["60 g", "120 g", "180 g", "240 g", "300 g"]);
        card.QuerySelector("h2")!.TextContent.ShouldBe("4:6 method (Tetsu Kasuya)");
        card.QuerySelector("button")!.Click();

        var saved = await Services.GetRequiredService<Dialed.Client.Data.IRepository<Recipe>>().ListAsync(Xunit.TestContext.Current.CancellationToken);
        var recipe = saved.ShouldHaveSingleItem();
        recipe.Name.ShouldBe("4:6 method (Tetsu Kasuya)");
        recipe.Steps.Count(s => s.Pattern == PourPattern.Spiral).ShouldBe(3);
    }
}
