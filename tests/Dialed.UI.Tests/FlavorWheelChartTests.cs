using Bunit;
using Dialed.Domain.Flavors;
using Dialed.Client.Data;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class FlavorWheelChartTests : UiTestContext
{
    [Fact]
    public void Draws_a_slice_per_branch_and_a_legend_that_says_the_same()
    {
        var chart = Render<FlavorWheelChart>(p => p.Add(c => c.Shares, [new(FlavorCategory.Fruity, 3), new(FlavorCategory.GreenVegetative, 1)]));

        chart.FindAll("circle.dialed-flavor-slice").Count.ShouldBe(2);
        chart.Find("circle.dialed-flavor-green").ShouldNotBeNull();
        chart.FindAll(".dialed-flavor-legend li").Select(li => li.TextContent.Trim()).ShouldBe(["Fruity · 3", "Green and herbal · 1"]);
        chart.Find("svg").GetAttribute("aria-label").ShouldBe("Tasting notes by flavour: Fruity · 3, Green and herbal · 1");
        chart.Find("text.dialed-flavor-total").TextContent.ShouldBe("4");
    }

    [Fact]
    public void Shows_nothing_without_notes_it_can_place()
    {
        Render<FlavorWheelChart>(p => p.Add(c => c.Shares, [])).Markup.Trim().ShouldBeEmpty();
    }

    private async Task<Bean> BeanWith(params string[] notes) =>
        await Services.GetRequiredService<IRepository<Bean>>().SaveAsync(new Bean { Name = "Guji", TastingNotes = notes }, Xunit.TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_bean_page_shows_the_wheel_when_the_notes_cover_two_branches()
    {
        var bean = await BeanWith("blueberry", "jasmine", "peach");

        var page = Render<Beans>(p => p.Add(b => b.Id, bean.Id));

        page.WaitForAssertion(() => page.Find("#bean-flavor-wheel").TextContent.ShouldContain("Fruity · 2"));
    }

    [Fact]
    public async Task The_bean_page_has_no_wheel_for_notes_from_a_single_branch()
    {
        var bean = await BeanWith("blueberry", "peach");

        var page = Render<Beans>(p => p.Add(b => b.Id, bean.Id));

        page.WaitForAssertion(() => page.Find("#bean-notes"));
        page.FindAll("#bean-flavor-wheel").ShouldBeEmpty();
    }
}
