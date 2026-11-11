using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.Model;
using Dialed.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class BrewCompareTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private IRepository<Brew> Brews => Services.GetRequiredService<IRepository<Brew>>();

    private async Task<(Bean Bean, Bag Bag)> BeanWithBag(string name = "Guji")
    {
        var bean = await Services.GetRequiredService<IRepository<Bean>>().SaveAsync(new Bean { Name = name }, Ct);
        var bag = await Services.GetRequiredService<IRepository<Bag>>().SaveAsync(new Bag { BeanId = bean.Id, WeightG = 250m }, Ct);
        return (bean, bag);
    }

    private static Brew NewBrew(Bag bag, int daysAgo, decimal dose = 18m, decimal? grind = null, int? time = null, int? rating = null, Taste? taste = null, string method = "v60") =>
        new() { BagId = bag.Id, MethodCode = method, DoseG = dose, GrindSetting = grind, TimeS = time, Rating = rating, Taste = taste ?? new Taste(), BrewedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo) };

    [Fact]
    public async Task The_brew_page_offers_the_other_brews_of_the_same_bean_and_opens_the_comparison()
    {
        var (_, guji) = await BeanWithBag("Guji");
        var (_, santos) = await BeanWithBag("Santos");
        var current = await Brews.SaveAsync(NewBrew(guji, 0), Ct);
        var other = await Brews.SaveAsync(NewBrew(guji, 2, rating: 4), Ct);
        await Brews.SaveAsync(NewBrew(santos, 1), Ct); // another bean: not offered
        var page = Render<History>(p => p.Add(x => x.Id, current.Id));

        page.FindAll("#history-compare-with option").Select(o => o.GetAttribute("value")).ShouldBe(["", other.Id]);
        page.Find("#history-compare-go").HasAttribute("disabled").ShouldBeTrue();

        page.Find("#history-compare-with").Change(other.Id);
        page.Find("#history-compare-go").HasAttribute("disabled").ShouldBeFalse();
        page.Find("#history-compare-go").Click();

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith($"history/compare/{current.Id}/{other.Id}");
    }

    [Fact]
    public async Task A_brew_that_is_the_only_one_of_its_bean_has_nothing_to_compare_with()
    {
        var (_, bag) = await BeanWithBag();
        var only = await Brews.SaveAsync(NewBrew(bag, 0), Ct);

        Render<History>(p => p.Add(x => x.Id, only.Id)).FindAll("#history-compare").ShouldBeEmpty();
    }

    [Fact]
    public async Task The_comparison_shows_both_brews_and_what_moved()
    {
        var (_, bag) = await BeanWithBag();
        var first = await Brews.SaveAsync(NewBrew(bag, 3, dose: 15m, grind: 20m, time: 170, rating: 3, taste: new Taste(Acidity: -1)), Ct);
        var second = await Brews.SaveAsync(NewBrew(bag, 1, dose: 16m, grind: 18m, time: 182, rating: 5), Ct);

        var page = Render<HistoryCompare>(p => p.Add(x => x.FirstId, first.Id).Add(x => x.SecondId, second.Id));

        string Cells(string parameter) => string.Join(" | ", page.Find($"tr[data-parameter={parameter}]").QuerySelectorAll("th,td").Select(c => c.TextContent.Trim()));
        Cells("Dose").ShouldBe("Dose | 15 g | 16 g | +1 g");
        Cells("Grind").ShouldContain("| 20 | 18 | −2");
        Cells("Time").ShouldBe("Time | 170 s | 182 s | +12 s");
        Cells("rating").ShouldBe("Rating | 3 ★ | 5 ★ | +2");
        Cells("taste-acidity").ShouldContain("Sour 1 | Balanced | +1");
        page.Find("tr[data-parameter=Dose]").ClassList.ShouldContain("differs");
        page.Find("tr[data-parameter=method] td:last-child").TextContent.ShouldBe("–"); // same method
        page.Find("#compare-first-link").GetAttribute("href").ShouldBe($"history/{first.Id}");
        page.Find("#compare-second-link").GetAttribute("href").ShouldBe($"history/{second.Id}");
        page.Find("#compare-bean").TextContent.ShouldBe("Guji");
        page.Find("#compare-first-link").TextContent.ShouldMatch(@"^\d{1,2} \w+, \d{1,2}[:.]\d{2}"); // a date and a clock time, not a stray "A"
    }

    [Fact]
    public async Task A_value_only_one_brew_has_is_shown_without_a_change_and_a_different_method_says_so()
    {
        var (_, bag) = await BeanWithBag();
        var first = await Brews.SaveAsync(NewBrew(bag, 2, time: 170), Ct);
        var second = await Brews.SaveAsync(NewBrew(bag, 1, method: "espresso"), Ct);

        var page = Render<HistoryCompare>(p => p.Add(x => x.FirstId, first.Id).Add(x => x.SecondId, second.Id));

        string Cells(string parameter) => string.Join(" | ", page.Find($"tr[data-parameter={parameter}]").QuerySelectorAll("th,td").Select(c => c.TextContent.Trim()));
        Cells("Time").ShouldBe("Time | 170 s | – | –");
        page.Find("tr[data-parameter=method] td:last-child").TextContent.ShouldBe("Different");
    }

    [Fact]
    public void An_unknown_brew_says_so()
    {
        Render<HistoryCompare>(p => p.Add(x => x.FirstId, "nope").Add(x => x.SecondId, "nada")).Markup.ShouldContain("no longer exists");
    }
}
