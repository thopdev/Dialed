using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.Model;
using Dialed.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class BestBrewRecipeUiTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private async Task<Bean> BeanWithBrews(params int[] ratings)
    {
        var bean = await Services.GetRequiredService<IRepository<Bean>>().SaveAsync(new Bean { Name = "Kiambu" }, Ct);
        var bag = await Services.GetRequiredService<IRepository<Bag>>().SaveAsync(new Bag { BeanId = bean.Id, WeightG = 250m }, Ct);
        var brews = Services.GetRequiredService<IRepository<Brew>>();
        foreach (var rating in ratings)
        {
            await brews.SaveAsync(new Brew { BagId = bag.Id, MethodCode = "v60", DoseG = 15m, WaterG = 250m, Rating = rating, BrewedAt = DateTimeOffset.Now.AddHours(-rating) }, Ct);
        }

        return bean;
    }

    [Fact]
    public async Task Two_good_brews_offer_a_recipe_that_is_saved_and_opened_on_tap()
    {
        var bean = await BeanWithBrews(5, 4);
        var page = Render<Beans>(p => p.Add(x => x.Id, bean.Id));

        var offer = page.WaitForElement("#bean-recipe-from-best");
        offer.TextContent.ShouldContain("Make a recipe from your best brews");
        offer.TextContent.ShouldContain("2 brews");
        offer.Click();

        var saved = (await Services.GetRequiredService<IRepository<Recipe>>().ListAsync(Ct)).ShouldHaveSingleItem();
        (saved.BeanId, saved.MethodCode, saved.DoseG, saved.WaterG).ShouldBe((bean.Id, "v60", 15m, 250m));
        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith($"recipes/{saved.Id}");
    }

    [Fact]
    public async Task Without_two_good_brews_there_is_no_offer()
    {
        var bean = await BeanWithBrews(5, 3);
        var page = Render<Beans>(p => p.Add(x => x.Id, bean.Id));

        page.WaitForElement("h1");
        page.FindAll("#bean-recipe-from-best").ShouldBeEmpty();
    }
}
