using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.Model;
using Dialed.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class HistoryTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private IRepository<Bean> Beans => Services.GetRequiredService<IRepository<Bean>>();

    private IRepository<Bag> Bags => Services.GetRequiredService<IRepository<Bag>>();

    private IRepository<Recipe> Recipes => Services.GetRequiredService<IRepository<Recipe>>();

    private IRepository<Brew> Brews => Services.GetRequiredService<IRepository<Brew>>();

    private async Task<Bag> BagOf(string beanName)
    {
        var bean = await Beans.SaveAsync(new Bean { Name = beanName }, Ct);
        return await Bags.SaveAsync(new Bag { BeanId = bean.Id, WeightG = 250m }, Ct);
    }

    private static Brew NewBrew(Bag bag, string method = "v60", int? rating = null, int minutesAgo = 0) =>
        new() { BagId = bag.Id, MethodCode = method, DoseG = 15m, WaterG = 240m, Rating = rating, BrewedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo) };

    [Fact]
    public void Empty_history_says_so()
    {
        Render<History>().Markup.ShouldContain("No brews yet");
    }

    [Fact]
    public async Task List_is_newest_first_and_filters_by_bean_method_person_and_rating()
    {
        var gesha = await BagOf("Gesha");
        var santos = await BagOf("Santos");
        await Brews.SaveAsync(NewBrew(gesha, "v60", 5, minutesAgo: 30), Ct);
        await Brews.SaveAsync(NewBrew(santos, "espresso", 3, minutesAgo: 10), Ct);
        var page = Render<History>();

        page.FindAll(".dialed-row-title").Select(t => t.TextContent).ShouldBe(["Santos", "Gesha"]);

        page.Find("#history-bean").Change(gesha.BeanId);
        page.FindAll(".dialed-row-title").Select(t => t.TextContent).ShouldBe(["Gesha"]);
        page.Find("#history-bean").Change("");

        page.Find("#history-method").Change("espresso");
        page.FindAll(".dialed-row-title").Select(t => t.TextContent).ShouldBe(["Santos"]);
        page.Find("#history-method").Change("");

        page.Find("#history-rating").Change("4");
        page.FindAll(".dialed-row-title").Select(t => t.TextContent).ShouldBe(["Gesha"]);
        page.Find("#history-rating").Change("0");

        page.Find("#history-person").Change("local-user");
        page.FindAll(".dialed-row-title").Count.ShouldBe(2);
        page.Find("#history-person option[value='local-user']").TextContent.ShouldBe("You");
    }

    [Fact]
    public async Task List_rows_show_who_brewed_with_an_avatar()
    {
        var bag = await BagOf("Gesha");
        await Brews.SaveAsync(NewBrew(bag), Ct);

        var page = Render<History>();

        page.Find(".dialed-avatar").TextContent.ShouldBe("Y");
    }

    [Fact]
    public async Task Detail_shows_actual_values_next_to_the_recipe_and_marks_differences()
    {
        var bag = await BagOf("Gesha");
        var recipe = await Recipes.SaveAsync(new Recipe { MethodCode = "espresso", Name = "House", DoseG = 18m, YieldG = 36m, TimeS = 28 }, Ct);
        var brew = await Brews.SaveAsync(new Brew
        {
            BagId = bag.Id, MethodCode = "espresso", RecipeId = recipe.Id, DoseG = 18m, YieldG = 40m, TimeS = 31,
            DrinkType = DrinkType.Cappuccino, MilkMl = 120, Rating = 4, Taste = new Taste(Acidity: 1, Sweetness: -1), Notes = "a bit long",
            BrewedAt = DateTimeOffset.UtcNow,
        }, Ct);

        var page = Render<History>(p => p.Add(x => x.Id, brew.Id));

        page.Find(".dialed-detail-pane h1").TextContent.ShouldBe("Gesha");
        page.Find("#history-person-name").TextContent.ShouldBe("You");
        var yield = page.Find("tr[data-parameter='Yield']");
        yield.ClassList.ShouldContain("differs");
        yield.QuerySelectorAll("td").Select(c => c.TextContent).ShouldBe(["36 g", "40 g"]);
        page.Find("tr[data-parameter='Dose']").ClassList.ShouldNotContain("differs");
        page.Find("tr[data-parameter='Time']").QuerySelectorAll("td").Select(c => c.TextContent).ShouldBe(["28 s", "31 s"]);
        page.Markup.ShouldContain("Cappuccino");
        page.Markup.ShouldContain("Bitter 1");
        page.Markup.ShouldContain("Flat 1");
        page.Markup.ShouldContain("a bit long");
        page.Find($"a[href='recipes/{recipe.Id}']").TextContent.ShouldContain("House");
    }

    [Fact]
    public async Task Own_brew_can_be_edited()
    {
        var bag = await BagOf("Gesha");
        var brew = await Brews.SaveAsync(NewBrew(bag), Ct);
        var page = Render<History>(p => p.Add(x => x.Id, brew.Id));

        page.FindAll(".dialed-page-actions button").First(b => b.TextContent == "Edit").Click();
        page.Find("#brew-dose").Change("16");
        page.Find("form").Submit();

        (await Brews.GetAsync(brew.Id, Ct))!.DoseG.ShouldBe(16m);
    }

    [Fact]
    public async Task Own_brew_can_be_deleted_after_confirming_and_the_coffee_returns_to_the_bag()
    {
        var bag = await BagOf("Gesha");
        var brew = await Brews.SaveAsync(NewBrew(bag), Ct);
        var page = Render<History>(p => p.Add(x => x.Id, brew.Id));

        page.FindAll(".dialed-page-actions button").First(b => b.TextContent == "Delete").Click();
        (await Brews.GetAsync(brew.Id, Ct)).ShouldNotBeNull();
        page.Find(".dialed-confirm .dialed-button-danger").Click();

        (await Brews.GetAsync(brew.Id, Ct)).ShouldBeNull();
        Render<Bags>(p => p.Add(x => x.Id, bag.Id)).Markup.ShouldContain("250 g of 250 g left");
    }

    [Fact]
    public async Task Another_members_brew_shows_their_name_and_cannot_be_changed()
    {
        var bag = await BagOf("Gesha");
        Context.UserId = "iris";
        var brew = await Brews.SaveAsync(NewBrew(bag), Ct);
        Context.UserId = "local-user";

        var page = Render<History>(p => p.Add(x => x.Id, brew.Id));

        page.Find("#history-person-name").TextContent.ShouldBe("iris");
        page.Find(".dialed-detail-pane .dialed-avatar").TextContent.ShouldBe("I");
        page.FindAll(".dialed-page-actions button").ShouldBeEmpty();
    }

    [Fact]
    public void Unknown_brew_shows_a_not_found_message()
    {
        Render<History>(p => p.Add(x => x.Id, "nope")).Markup.ShouldContain("no longer exists");
    }

    [Fact]
    public void More_page_links_to_the_history()
    {
        Render<More>().Find("a[href='history']").ShouldNotBeNull();
    }
}
