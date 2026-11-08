using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.Model;
using Dialed.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class RecipeVersionTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private IRepository<Recipe> Recipes => Services.GetRequiredService<IRepository<Recipe>>();

    private IRepository<Brew> Brews => Services.GetRequiredService<IRepository<Brew>>();

    private async Task<Recipe> BrewedRecipe(decimal dose = 15m, int? rating = null)
    {
        var recipe = await Recipes.SaveAsync(new Recipe { MethodCode = "v60", Name = "Morning", DoseG = dose, WaterG = 240m }, Ct);
        await Brews.SaveAsync(new Brew { BagId = "bag", MethodCode = "v60", DoseG = dose, RecipeId = recipe.Id, Rating = rating, BrewedAt = DateTimeOffset.UtcNow }, Ct);
        return recipe;
    }

    private static void EditDose(IRenderedComponent<Recipes> page, string dose)
    {
        page.FindAll(".dialed-page-actions button").First(b => b.TextContent == "Edit").Click();
        page.Find("#recipe-dose").Change(dose);
        page.Find("form").Submit();
    }

    [Fact]
    public async Task Editing_a_recipe_without_brews_saves_in_place_without_asking()
    {
        var recipe = await Recipes.SaveAsync(new Recipe { MethodCode = "v60", Name = "Morning", DoseG = 15m, WaterG = 240m }, Ct);
        var page = Render<Recipes>(p => p.Add(x => x.Id, recipe.Id));

        EditDose(page, "16");

        page.FindAll("#recipe-version-prompt").ShouldBeEmpty();
        (await Recipes.ListAsync(Ct)).Count.ShouldBe(1);
        (await Recipes.GetAsync(recipe.Id, Ct))!.DoseG.ShouldBe(16m);
    }

    [Fact]
    public async Task Editing_a_brewed_recipe_asks_what_to_do_and_changes_nothing_yet()
    {
        var recipe = await BrewedRecipe();
        var page = Render<Recipes>(p => p.Add(x => x.Id, recipe.Id));

        EditDose(page, "16");

        page.Find("#recipe-version-prompt").TextContent.ShouldContain("This recipe has brews");
        (await Recipes.GetAsync(recipe.Id, Ct))!.DoseG.ShouldBe(15m);
        (await Recipes.ListAsync(Ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Save_as_new_version_keeps_the_old_one_superseded_and_opens_the_new_one()
    {
        var recipe = await BrewedRecipe();
        var page = Render<Recipes>(p => p.Add(x => x.Id, recipe.Id));
        EditDose(page, "16");

        page.Find("#recipe-save-new-version").Click();

        var all = await Recipes.ListAsync(Ct);
        all.Count.ShouldBe(2);
        var old = all.Single(r => r.Id == recipe.Id);
        var created = all.Single(r => r.Id != recipe.Id);
        old.IsSuperseded.ShouldBeTrue();
        old.DoseG.ShouldBe(15m);
        created.ParentRecipeId.ShouldBe(recipe.Id);
        created.DoseG.ShouldBe(16m);
        created.IsSuperseded.ShouldBeFalse();
        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith($"recipes/{created.Id}");
    }

    [Fact]
    public async Task Update_this_version_overwrites_without_a_new_version()
    {
        var recipe = await BrewedRecipe();
        var page = Render<Recipes>(p => p.Add(x => x.Id, recipe.Id));
        EditDose(page, "16");

        page.Find("#recipe-update-version").Click();

        var all = await Recipes.ListAsync(Ct);
        all.Count.ShouldBe(1);
        all[0].DoseG.ShouldBe(16m);
        all[0].IsSuperseded.ShouldBeFalse();
    }

    [Fact]
    public async Task Cancelling_the_prompt_keeps_editing()
    {
        var recipe = await BrewedRecipe();
        var page = Render<Recipes>(p => p.Add(x => x.Id, recipe.Id));
        EditDose(page, "16");

        page.FindAll("#recipe-version-prompt button").First(b => b.TextContent == "Cancel").Click();

        page.FindAll("#recipe-version-prompt").ShouldBeEmpty();
        page.Find("form").ShouldNotBeNull();
        (await Recipes.GetAsync(recipe.Id, Ct))!.DoseG.ShouldBe(15m);
    }

    [Fact]
    public async Task The_list_only_shows_the_current_version()
    {
        var recipe = await BrewedRecipe();
        var page = Render<Recipes>(p => p.Add(x => x.Id, recipe.Id));
        EditDose(page, "16");
        page.Find("#recipe-save-new-version").Click();

        var list = Render<Recipes>();

        list.FindAll(".dialed-list .dialed-row-title").Select(t => t.TextContent).Where(t => t == "Morning").Count().ShouldBe(1);
    }

    [Fact]
    public async Task Timeline_shows_versions_with_changes_and_average_rating()
    {
        var v1 = await BrewedRecipe(rating: 3);
        await Brews.SaveAsync(new Brew { BagId = "bag", MethodCode = "v60", DoseG = 15m, RecipeId = v1.Id, Rating = 5, BrewedAt = DateTimeOffset.UtcNow }, Ct);
        var created = RecipeVersions.NewVersion((await Recipes.GetAsync(v1.Id, Ct))!, (await Recipes.GetAsync(v1.Id, Ct))! with { DoseG = 16m });
        var v2 = await Recipes.SaveAsync(created.NewVersion, Ct);
        await Recipes.SaveAsync(created.Superseded, Ct);

        var page = Render<Recipes>(p => p.Add(x => x.Id, v2.Id));

        var items = page.FindAll(".dialed-versions li");
        items.Count.ShouldBe(2);
        items[0].TextContent.ShouldContain("Version 1");
        items[0].TextContent.ShouldContain("4.0 ★ · 2 brew(s)");
        items[1].TextContent.ShouldContain("Version 2");
        items[1].TextContent.ShouldContain("Current");
        items[1].TextContent.ShouldContain("Dose 15 g → 16 g");
        items[1].TextContent.ShouldContain("No brews yet");
    }

    [Fact]
    public async Task An_older_version_can_be_made_current_again()
    {
        var v1 = await BrewedRecipe();
        var (created, superseded) = RecipeVersions.NewVersion(v1, v1 with { DoseG = 16m });
        var v2 = await Recipes.SaveAsync(created, Ct);
        await Recipes.SaveAsync(superseded, Ct);
        var page = Render<Recipes>(p => p.Add(x => x.Id, v1.Id));

        page.Find("#recipe-older-version").TextContent.ShouldContain("older version");
        page.Find("#recipe-older-version button").Click();

        (await Recipes.GetAsync(v1.Id, Ct))!.IsSuperseded.ShouldBeFalse();
        (await Recipes.GetAsync(v2.Id, Ct))!.IsSuperseded.ShouldBeTrue();
        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith($"recipes/{v1.Id}");
    }

    [Fact]
    public async Task Deleting_the_current_version_brings_back_the_previous_one()
    {
        var v1 = await BrewedRecipe();
        var (created, superseded) = RecipeVersions.NewVersion(v1, v1 with { DoseG = 16m });
        var v2 = await Recipes.SaveAsync(created, Ct);
        await Recipes.SaveAsync(superseded, Ct);
        var page = Render<Recipes>(p => p.Add(x => x.Id, v2.Id));

        page.FindAll(".dialed-page-actions button").First(b => b.TextContent == "Delete").Click();
        page.Find(".dialed-confirm .dialed-button-danger").Click();

        (await Recipes.GetAsync(v2.Id, Ct)).ShouldBeNull();
        (await Recipes.GetAsync(v1.Id, Ct))!.IsSuperseded.ShouldBeFalse();
    }
}
