using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

/// <summary>Recipes and history made easy to scan (#215): method icons and key numbers, the recipe grid and step timeline, history by day, the taste chart.</summary>
public sealed class RecipesHistoryPolishTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private IRepository<Bean> Beans => Services.GetRequiredService<IRepository<Bean>>();

    private IRepository<Bag> Bags => Services.GetRequiredService<IRepository<Bag>>();

    private IRepository<Brew> Brews => Services.GetRequiredService<IRepository<Brew>>();

    private IRepository<Recipe> Recipes => Services.GetRequiredService<IRepository<Recipe>>();

    private async Task<Bag> BagOf(string name)
    {
        var bean = await Beans.SaveAsync(new Bean { Name = name }, Ct);
        return await Bags.SaveAsync(new Bag { BeanId = bean.Id, WeightG = 250m }, Ct);
    }

    private Task<Brew> Brewed(Bag bag, DateTimeOffset at, string method = "espresso", DrinkType drink = DrinkType.Espresso, int? rating = null, Taste? taste = null, string? recipeId = null) =>
        Brews.SaveAsync(new Brew { BagId = bag.Id, MethodCode = method, DrinkType = drink, DoseG = 18m, YieldG = 36m, Rating = rating, Taste = taste ?? new Taste(), BrewedAt = at, RecipeId = recipeId }, Ct);

    // ---- History list -------------------------------------------------------------------------

    [Fact]
    public async Task History_is_grouped_by_day_newest_first()
    {
        var bag = await BagOf("Gesha");
        // Noon today, so the test does not depend on how close to midnight it runs.
        var now = new DateTimeOffset(DateTime.Today.AddHours(12));
        await Brewed(bag, now.AddMinutes(-1));
        await Brewed(bag, now.AddMinutes(-2));
        await Brewed(bag, now.AddDays(-1));
        var older = now.AddDays(-3);
        await Brewed(bag, older);

        var page = Render<History>();

        var days = page.FindAll(".dialed-day-group");
        days.Select(d => d.QuerySelector("h2 strong")!.TextContent).ShouldBe(["Today", "Yesterday", older.ToString("dddd d MMMM")]);
        days[0].QuerySelector(".dialed-day-sub")!.TextContent.ShouldBe($"{now.ToString("ddd d MMM")} · 2 brews");
        days[1].QuerySelector(".dialed-day-sub")!.TextContent.ShouldEndWith("· 1 brew");
        days[2].QuerySelector(".dialed-day-sub")!.TextContent.ShouldBe("1 brew");
        days[0].QuerySelectorAll(".dialed-row").Length.ShouldBe(2);
        days[0].GetAttribute("aria-labelledby").ShouldBe(days[0].QuerySelector("h2")!.Id);
        page.Find("#history-count").TextContent.ShouldBe("4 brews");
    }

    [Fact]
    public async Task History_rows_show_method_amounts_rating_and_what_was_off_in_the_taste()
    {
        var bag = await BagOf("Gesha");
        await Brewed(bag, DateTimeOffset.Now, rating: 4, taste: new Taste(Acidity: -1, Body: 2));

        var row = Render<History>().Find(".dialed-day-group .dialed-row");

        row.QuerySelector(".dialed-method-icon").ShouldNotBeNull();
        row.QuerySelector(".dialed-row-subtitle")!.TextContent.ShouldEndWith(" · Espresso · 18 g → 36 g");
        row.QuerySelector(".dialed-row-subtitle")!.TextContent.ShouldNotContain("Espresso · Espresso");
        row.QuerySelectorAll(".dialed-chip").Select(c => c.TextContent).ShouldBe(["Sour 1", "Heavy 2"]);
        row.QuerySelector("[role=img]")!.GetAttribute("aria-label").ShouldBe("4 of 5 stars");
    }

    [Fact]
    public async Task History_filters_are_folded_away_and_count_what_is_set()
    {
        var bag = await BagOf("Gesha");
        await Brewed(bag, DateTimeOffset.Now, rating: 5);
        var page = Render<History>();

        page.Find("#history-filters").HasAttribute("hidden").ShouldBeTrue();
        page.Find("#history-filters-toggle").Click();
        page.Find("#history-rating").Change("4");

        page.Find("#history-filters-toggle").GetAttribute("aria-expanded").ShouldBe("true");
        page.Find("#history-filters-toggle").TextContent.Trim().ShouldBe("Filters (1)");
    }

    [Fact]
    public async Task Household_members_are_shown_by_name_when_signed_in()
    {
        Tokens.Value = "token";
        HouseholdServer.Members = [("Bob Barista", "bob-id")];
        var bag = await BagOf("Gesha");
        Context.UserId = "bob-id";
        await Brewed(bag, DateTimeOffset.Now.AddMinutes(-3));
        Context.UserId = "local-user";
        await Brewed(bag, DateTimeOffset.Now);

        var page = Render<History>();

        page.WaitForAssertion(() => page.FindAll(".dialed-row-person").Select(p => p.TextContent.Trim()).ShouldContain("BB Bob Barista"));
    }

    // ---- Brew detail --------------------------------------------------------------------------

    [Fact]
    public async Task Brew_detail_names_the_drink_once_and_shows_taste_as_a_chart()
    {
        var bag = await BagOf("Gesha");
        var brew = await Brewed(bag, DateTimeOffset.Now, rating: 5, taste: new Taste(Acidity: -2, Sweetness: 1));

        var page = Render<History>(p => p.Add(x => x.Id, brew.Id));

        page.Find("#history-what").TextContent.ShouldBe("Espresso");
        page.Find("#history-when").TextContent.ShouldStartWith("Today, ");
        var chart = page.Find("#history-taste");
        chart.GetAttribute("role").ShouldBe("img");
        chart.GetAttribute("aria-label").ShouldBe("Acidity: sour 2. Strength: balanced. Body: balanced. Sweetness: sweet 1.");
        chart.QuerySelector("[data-axis=acidity] .dialed-taste-chart-fill")!.GetAttribute("style").ShouldBe("left: 0%; width: 50%");
        chart.QuerySelector("[data-axis=sweetness] .dialed-taste-chart-fill")!.GetAttribute("style").ShouldBe("left: 50%; width: 25%");
        page.Find("#history-edit").GetAttribute("aria-label").ShouldBe("Edit");
    }

    [Fact]
    public async Task Brew_detail_with_milk_names_drink_and_method_and_links_the_recipe_as_a_card()
    {
        var bag = await BagOf("Gesha");
        var recipe = await Recipes.SaveAsync(new Recipe { MethodCode = "espresso", Name = "House", DoseG = 18m, YieldG = 36m }, Ct);
        var brew = await Brewed(bag, DateTimeOffset.Now, drink: DrinkType.FlatWhite, recipeId: recipe.Id);

        var page = Render<History>(p => p.Add(x => x.Id, brew.Id));

        page.Find("#history-what").TextContent.ShouldStartWith("Flat white · Espresso");
        page.Find("#history-recipe").GetAttribute("href").ShouldBe($"recipes/{recipe.Id}");
        page.Find("#history-recipe .dialed-row-title").TextContent.ShouldBe("House");
    }

    // ---- Recipes ------------------------------------------------------------------------------

    [Fact]
    public async Task Recipe_rows_show_key_numbers_and_the_average_rating_of_their_brews()
    {
        var recipe = await Recipes.SaveAsync(new Recipe { MethodCode = "v60", Name = "Morning", DoseG = 15m, WaterG = 250m, TempC = 94m, TimeS = 195 }, Ct);
        var bag = await BagOf("Gesha");
        await Brewed(bag, DateTimeOffset.Now, method: "v60", rating: 5, recipeId: recipe.Id);
        await Brewed(bag, DateTimeOffset.Now, method: "v60", rating: 4, recipeId: recipe.Id);

        var row = Render<Recipes>().Find($"[data-recipe='{recipe.Id}']");

        row.QuerySelectorAll(".dialed-key-numbers .dialed-chip").Select(c => c.TextContent).ShouldBe(["15 g", "250 g", "1:16.7", "94 °C", "3:15"]);
        row.QuerySelector("[role=img]")!.GetAttribute("aria-label").ShouldBe("5 of 5 stars");
    }

    [Fact]
    public async Task Recipe_detail_shows_parameters_as_tiles_and_steps_as_a_timeline()
    {
        var recipe = await Recipes.SaveAsync(new Recipe
        {
            MethodCode = "v60",
            Name = "Morning",
            DoseG = 15m,
            WaterG = 250m,
            TempC = 94m,
            TimeS = 180,
            Steps = [new RecipeStep(RecipeStepKind.Bloom, 0, 45, 45m), new RecipeStep(RecipeStepKind.Pour, 45, null, 250m)],
        }, Ct);

        var page = Render<Recipes>(p => p.Add(x => x.Id, recipe.Id));

        page.FindAll("#recipe-parameters .dialed-stat-label").Select(l => l.TextContent).ShouldBe(["Dose", "Water", "Ratio", "Temp", "Time"]);
        page.FindAll("#recipe-parameters .dialed-stat-value").Select(v => v.TextContent).ShouldBe(["15 g", "250 g", "1:16.7", "94 °C", "3:00"]);
        page.FindAll("#recipe-steps .dialed-step-time").Select(t => t.TextContent).ShouldBe(["0:00", "0:45"]);
        page.Find("#recipe-hero .dialed-method-icon").ShouldNotBeNull();
        page.Find("#recipe-stats").TextContent.ShouldBe("No brews yet");
        page.Find("#recipe-favourite").GetAttribute("aria-label").ShouldBe("Favourite");
        page.Find("#recipe-favourite").GetAttribute("aria-pressed").ShouldBe("false");
    }

    [Fact]
    public void Taste_chart_reads_as_one_sentence_and_hides_the_bars()
    {
        var chart = Render<TasteChart>(p => p.Add(x => x.Taste, new Taste()));

        chart.Find("[role=img]").GetAttribute("aria-label").ShouldBe("Acidity: balanced. Strength: balanced. Body: balanced. Sweetness: balanced.");
        chart.FindAll(".dialed-taste-chart-row").ShouldAllBe(r => r.GetAttribute("aria-hidden") == "true");
        chart.FindAll(".dialed-taste-chart-fill").ShouldAllBe(f => f.GetAttribute("style") == "left: 50%; width: 0%");
    }
}
