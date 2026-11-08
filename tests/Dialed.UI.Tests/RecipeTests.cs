using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.Equipment;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class RecipeTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private IRepository<Recipe> Recipes => Services.GetRequiredService<IRepository<Recipe>>();

    private IRepository<EquipmentItem> Gear => Services.GetRequiredService<IRepository<EquipmentItem>>();

    private IRepository<MethodDefault> Defaults => Services.GetRequiredService<IRepository<MethodDefault>>();

    private IRenderedComponent<RecipeForm> RenderForm(Action<Recipe>? onSubmit = null, string method = "v60", IReadOnlyList<EquipmentItem>? gear = null, IReadOnlyList<MethodDefault>? defaults = null) =>
        Render<RecipeForm>(p => p
            .Add(x => x.MethodCode, method)
            .Add(x => x.Gear, gear ?? [])
            .Add(x => x.Defaults, defaults ?? [])
            .Add(x => x.OnSubmit, EventCallback.Factory.Create<Recipe>(this, r => onSubmit?.Invoke(r))));

    // ---- Form ---------------------------------------------------------------------------------

    [Fact]
    public void Form_requires_a_name()
    {
        Recipe? submitted = null;
        var form = RenderForm(r => submitted = r);

        form.Find("form").Submit();

        form.Markup.ShouldContain("Enter a name.");
        submitted.ShouldBeNull();
    }

    [Fact]
    public void Pour_over_asks_for_water_and_steps_but_not_yield()
    {
        var form = RenderForm(method: "v60");

        form.FindAll("label").Select(l => l.TextContent).ShouldContain(t => t.StartsWith("Water", StringComparison.Ordinal));
        form.FindAll("#recipe-add-step").Count.ShouldBe(1);
        form.FindAll("#recipe-pre").ShouldBeEmpty();
    }

    [Fact]
    public void Espresso_asks_for_yield_and_preinfusion_but_not_steps()
    {
        var form = RenderForm(method: "espresso");

        form.FindAll("label").Select(l => l.TextContent).ShouldContain(t => t.StartsWith("Yield", StringComparison.Ordinal));
        form.FindAll("#recipe-pre").Count.ShouldBe(1);
        form.FindAll("#recipe-add-step").ShouldBeEmpty();
    }

    [Fact]
    public void Editing_any_two_of_dose_water_ratio_calculates_the_third()
    {
        var form = RenderForm();

        form.Find("#recipe-dose").Change("15");
        form.Find("#recipe-liquid").Change("240");
        form.Find("#recipe-ratio").GetAttribute("value").ShouldBe("16");
        form.Find("#recipe-per-litre").TextContent.ShouldContain("62.5");

        form.Find("#recipe-ratio").Change("15");
        form.Find("#recipe-dose").GetAttribute("value").ShouldBe("16");
    }

    [Fact]
    public void Form_submits_a_pour_over_recipe_with_steps_in_time_order()
    {
        Recipe? submitted = null;
        var form = RenderForm(r => submitted = r);
        form.Find("#recipe-name").Input("Morning V60");
        form.Find("#recipe-dose").Change("15");
        form.Find("#recipe-liquid").Change("240");
        form.Find("#recipe-temp").Change("94");
        form.Find("#recipe-time").Change("180");
        form.Find("#recipe-filter").Change("Cafec Abaca");
        form.Find("#recipe-add-step").Click();
        form.Find("#step-water-0").Change("45");
        form.Find("#step-duration-0").Change("30");
        form.Find("#recipe-add-step").Click();
        form.Find("#step-at-1").Change("45");
        form.Find("#step-water-1").Change("240");
        form.Find("#step-note-1").Change("slow spiral");

        form.Find("form").Submit();

        submitted.ShouldNotBeNull();
        submitted.MethodCode.ShouldBe("v60");
        submitted.Name.ShouldBe("Morning V60");
        submitted.DoseG.ShouldBe(15m);
        submitted.WaterG.ShouldBe(240m);
        submitted.YieldG.ShouldBeNull();
        submitted.Ratio.ShouldBe(16m);
        submitted.TempC.ShouldBe(94m);
        submitted.TimeS.ShouldBe(180);
        submitted.Filter.ShouldBe("Cafec Abaca");
        submitted.Steps.Count.ShouldBe(2);
        submitted.Steps[0].ShouldBe(new RecipeStep(RecipeStepKind.Bloom, 0, 30, 45m, null));
        submitted.Steps[1].ShouldBe(new RecipeStep(RecipeStepKind.Pour, 45, null, 240m, "slow spiral"));
    }

    [Fact]
    public void Espresso_recipe_stores_yield_and_no_steps()
    {
        Recipe? submitted = null;
        var form = RenderForm(r => submitted = r, method: "espresso");
        form.Find("#recipe-name").Input("House shot");
        form.Find("#recipe-dose").Change("18");
        form.Find("#recipe-liquid").Change("36");
        form.Find("#recipe-pre").Change("5");

        form.Find("form").Submit();

        submitted!.YieldG.ShouldBe(36m);
        submitted.WaterG.ShouldBeNull();
        submitted.PreinfusionS.ShouldBe(5);
        submitted.Steps.ShouldBeEmpty();
    }

    [Fact]
    public void Steps_can_be_moved_and_removed()
    {
        var form = RenderForm();
        form.Find("#recipe-add-step").Click();
        form.Find("#recipe-add-step").Click();
        form.Find("#step-note-0").Change("first");
        form.Find("#step-note-1").Change("second");

        form.Find("[data-step='1'] button[aria-label='Move up']").Click();
        form.Find("#step-note-0").GetAttribute("value").ShouldBe("second");

        form.Find("[data-step='0'] button[aria-label='Remove step']").Click();
        form.FindAll("[data-step]").Count.ShouldBe(1);
        form.Find("#step-note-0").GetAttribute("value").ShouldBe("first");
    }

    [Fact]
    public void New_recipe_starts_with_the_default_grinder_for_the_method()
    {
        var grinder = new EquipmentItem { Id = "g1", Kind = EquipmentKind.Grinder, Brand = "Comandante", GrindScale = GrindScale.Clicks(40m) };

        var form = RenderForm(gear: [grinder], defaults: [new MethodDefault { MethodCode = "v60", GrinderId = "g1" }]);

        form.Find("#recipe-grinder").GetAttribute("value").ShouldBe("g1");
    }

    [Fact]
    public void Grind_setting_needs_a_grinder_with_a_scale()
    {
        var form = RenderForm();
        form.Markup.ShouldContain("Choose a grinder to set the grind.");

        var grinder = new EquipmentItem { Id = "g1", Kind = EquipmentKind.Grinder, Brand = "Comandante", GrindScale = GrindScale.Clicks(40m) };
        var withGrinder = RenderForm(gear: [grinder], defaults: [new MethodDefault { MethodCode = "v60", GrinderId = "g1" }]);

        withGrinder.Markup.ShouldNotContain("Choose a grinder");
        withGrinder.Find(".dialed-grind-input input").ShouldNotBeNull();
    }

    // ---- Page ---------------------------------------------------------------------------------

    [Fact]
    public void Empty_list_invites_the_first_recipe()
    {
        var page = Render<Recipes>();

        page.Markup.ShouldContain("Your recipes will show up here.");
        page.Find("a[href='recipes/new']").ShouldNotBeNull();
    }

    [Fact]
    public async Task List_shows_recipes_with_method_and_ratio()
    {
        await Recipes.SaveAsync(new Recipe { MethodCode = "v60", Name = "Morning", DoseG = 15m, WaterG = 240m }, Ct);

        var page = Render<Recipes>();

        page.Find(".dialed-row-title").TextContent.ShouldBe("Morning");
        page.Markup.ShouldContain("Hario V60 · 1:16");
    }

    [Fact]
    public async Task Detail_shows_parameters_and_steps()
    {
        var recipe = await Recipes.SaveAsync(new Recipe
        {
            MethodCode = "v60",
            Name = "Morning",
            DoseG = 15m,
            WaterG = 240m,
            TempC = 94m,
            Steps = [new RecipeStep(RecipeStepKind.Bloom, 0, 30, 45m), new RecipeStep(RecipeStepKind.Pour, 45, null, 240m, "slow")],
        }, Ct);

        var page = Render<Recipes>(p => p.Add(x => x.Id, recipe.Id));

        page.Find(".dialed-detail-pane h1").TextContent.ShouldBe("Morning");
        page.Markup.ShouldContain("1:16");
        page.Markup.ShouldContain("94");
        page.FindAll(".dialed-step-list li").Select(l => l.TextContent).ShouldBe(["0 s · Bloom · to 45 g · 30 s", "45 s · Pour · to 240 g · slow"]);
    }

    [Fact]
    public async Task Creating_a_recipe_saves_it_and_opens_it()
    {
        var page = Render<Recipes>(p => p.Add(x => x.Id, "new"));
        page.Find("#recipe-name").Input("First");

        page.Find("form").Submit();

        var all = await Recipes.ListAsync(Ct);
        all.Select(r => r.Name).ShouldBe(["First"]);
        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith($"recipes/{all[0].Id}");
    }

    [Fact]
    public async Task New_recipe_page_uses_the_default_grinder()
    {
        var grinder = await Gear.SaveAsync(new EquipmentItem { Kind = EquipmentKind.Grinder, Brand = "Comandante", GrindScale = GrindScale.Clicks(40m) }, Ct);
        var first = BrewMethodCatalogFirst();
        await Defaults.SaveAsync(new MethodDefault { MethodCode = first, GrinderId = grinder.Id }, Ct);

        var page = Render<Recipes>(p => p.Add(x => x.Id, "new"));

        page.Find("#recipe-grinder").GetAttribute("value").ShouldBe(grinder.Id);
    }

    private static string BrewMethodCatalogFirst() => Dialed.Domain.BrewMethods.BrewMethodCatalog.Default.Methods[0].Code;

    [Fact]
    public async Task Editing_a_recipe_saves_the_change()
    {
        var recipe = await Recipes.SaveAsync(new Recipe { MethodCode = "v60", Name = "Morning" }, Ct);
        var page = Render<Recipes>(p => p.Add(x => x.Id, recipe.Id));

        page.FindAll(".dialed-page-actions button").First(b => b.TextContent == "Edit").Click();
        page.Find("#recipe-name").Input("Evening");
        page.Find("form").Submit();

        (await Recipes.GetAsync(recipe.Id, Ct))!.Name.ShouldBe("Evening");
    }

    [Fact]
    public async Task Deleting_a_recipe_asks_first()
    {
        var recipe = await Recipes.SaveAsync(new Recipe { MethodCode = "v60", Name = "Morning" }, Ct);
        var page = Render<Recipes>(p => p.Add(x => x.Id, recipe.Id));

        page.FindAll(".dialed-page-actions button").First(b => b.TextContent == "Delete").Click();
        (await Recipes.GetAsync(recipe.Id, Ct)).ShouldNotBeNull();
        page.Find(".dialed-confirm .dialed-button-danger").Click();

        (await Recipes.GetAsync(recipe.Id, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Default_templates_are_read_only()
    {
        var recipe = await Recipes.SaveAsync(new Recipe { MethodCode = "v60", Name = "Template", IsDefaultTemplate = true }, Ct);

        var page = Render<Recipes>(p => p.Add(x => x.Id, recipe.Id));

        page.FindAll(".dialed-page-actions button").ShouldBeEmpty();
    }
}
