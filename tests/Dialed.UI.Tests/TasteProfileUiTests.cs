using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.Model;
using Dialed.Domain.Statistics;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class TasteProfileUiTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public void Each_axis_is_one_image_that_says_which_way_it_leans_or_that_it_is_balanced()
    {
        var result = new BeanTasteResult(3, [new(TasteAxis.Acidity, -1.5), new(TasteAxis.Strength, 0.2), new(TasteAxis.Body, 1.0), new(TasteAxis.Sweetness, -0.6)]);

        var bars = Render<TasteProfileBars>(p => p.Add(x => x.Result, result));

        bars.FindAll("[role=img]").Select(r => r.GetAttribute("aria-label")).ShouldBe(["Acidity: leans Sour", "Strength: about balanced", "Body: leans Heavy", "Sweetness: leans Flat"]);
        bars.FindAll(".dialed-taste-fill").Select(f => f.ClassList.Contains("left")).ShouldBe([true, false, false, true]);
        bars.FindAll(".dialed-taste-fill").Select(f => f.GetAttribute("style")).First().ShouldBe("width: 38%");
    }

    [Fact]
    public async Task The_bean_page_shows_the_profile_after_two_brews_that_were_off()
    {
        var bean = await Services.GetRequiredService<IRepository<Bean>>().SaveAsync(new Bean { Name = "Kiambu" }, Ct);
        var bag = await Services.GetRequiredService<IRepository<Bag>>().SaveAsync(new Bag { BeanId = bean.Id, WeightG = 250m }, Ct);
        var brews = Services.GetRequiredService<IRepository<Brew>>();
        await brews.SaveAsync(new Brew { BagId = bag.Id, MethodCode = "v60", DoseG = 15m, Taste = new Taste(Acidity: -1), BrewedAt = DateTimeOffset.Now }, Ct);
        var page = Render<Beans>(p => p.Add(x => x.Id, bean.Id));
        page.WaitForElement("h1");
        page.FindAll("#bean-taste-profile").ShouldBeEmpty();

        await brews.SaveAsync(new Brew { BagId = bag.Id, MethodCode = "v60", DoseG = 15m, Taste = new Taste(Acidity: -2), BrewedAt = DateTimeOffset.Now }, Ct);
        var again = Render<Beans>(p => p.Add(x => x.Id, bean.Id));

        again.WaitForElement("#bean-taste-profile");
        again.Markup.ShouldContain("How it tends to taste");
    }
}
