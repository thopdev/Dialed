using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.Model;
using Dialed.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class BeanCompareTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private async Task<Bean> Bean(string name, params (int Rating, Taste Taste)[] brews)
    {
        var bean = await Services.GetRequiredService<IRepository<Bean>>().SaveAsync(new Bean { Name = name }, Ct);
        var bag = await Services.GetRequiredService<IRepository<Bag>>().SaveAsync(new Bag { BeanId = bean.Id, WeightG = 250m }, Ct);
        var store = Services.GetRequiredService<IRepository<Brew>>();
        foreach (var (rating, taste) in brews)
        {
            await store.SaveAsync(new Brew { BagId = bag.Id, MethodCode = "v60", DoseG = 15m, Rating = rating, Taste = taste, BrewedAt = DateTimeOffset.Now }, Ct);
        }

        return bean;
    }

    [Fact]
    public async Task The_page_puts_two_beans_side_by_side_and_marks_what_differs()
    {
        var kiambu = await Bean("Kiambu", (5, new Taste(Acidity: -2)), (4, new Taste(Acidity: -1)));
        var brazil = await Bean("Brazil", (3, new Taste(Acidity: 2)), (3, new Taste(Acidity: 1)), (2, new Taste()));

        var page = Render<BeanCompare>(p => p.Add(x => x.FirstId, kiambu.Id).Add(x => x.SecondId, brazil.Id));

        page.WaitForElement("#bean-compare-table");
        string Cells(string row) => string.Join("|", page.Find($"tr[data-row={row}]").QuerySelectorAll("td").Select(c => c.TextContent.Trim()));
        Cells("brews").ShouldBe("2|3");
        Cells("rating").ShouldBe("4.5 ★ (2 rated)|2.7 ★ (3 rated)");
        Cells("good").ShouldBe("2|0");
        Cells("method").ShouldBe("Hario V60|Hario V60");
        Cells("taste-acidity").ShouldBe("Sour|Bitter");
        Cells("taste-strength").ShouldBe("Balanced|Balanced");
        page.Find("tr[data-row=method]").ClassList.ShouldNotContain("differs");
        page.Find("tr[data-row=rating]").ClassList.ShouldContain("differs");
        page.Find("#bean-compare-first").TextContent.ShouldBe("Kiambu");
    }

    [Fact]
    public async Task An_unknown_bean_says_so()
    {
        var one = await Bean("Kiambu");

        Render<BeanCompare>(p => p.Add(x => x.FirstId, one.Id).Add(x => x.SecondId, "nope")).WaitForElement(".dialed-empty").TextContent.ShouldContain("One of these beans no longer exists.");
    }

    [Fact]
    public async Task The_bean_page_offers_the_other_beans_and_opens_the_comparison()
    {
        var kiambu = await Bean("Kiambu");
        var brazil = await Bean("Brazil");
        var page = Render<Beans>(p => p.Add(x => x.Id, kiambu.Id));

        page.WaitForElement("#bean-compare-with").QuerySelectorAll("option").Select(o => o.TextContent.Trim()).ShouldBe(["Pick a bean", "Brazil"]);
        page.Find("#bean-compare-go").HasAttribute("disabled").ShouldBeTrue();
        page.Find("#bean-compare-with").Change(brazil.Id);
        page.Find("#bean-compare-go").Click();

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith($"beans/compare/{kiambu.Id}/{brazil.Id}");
    }
}
