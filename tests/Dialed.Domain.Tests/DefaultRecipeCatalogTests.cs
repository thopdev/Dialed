using System.Text;
using System.Text.Json;
using Dialed.Domain.BrewMethods;
using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class DefaultRecipeCatalogTests
{
    private static DefaultRecipeCatalog Parse(string json) =>
        DefaultRecipeCatalog.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)), BrewMethodCatalog.Default);

    [Fact]
    public void Every_seeded_method_has_exactly_one_default()
    {
        var catalog = DefaultRecipeCatalog.Default;

        catalog.Version.ShouldBeGreaterThan(0);
        catalog.Recipes.Select(r => r.MethodCode).Order().ShouldBe(BrewMethodCatalog.Default.Methods.Select(m => m.Code).Order());
    }

    [Fact]
    public void Espresso_default_is_one_to_two_around_twenty_five_to_thirty_seconds()
    {
        var espresso = DefaultRecipeCatalog.Default.ForMethod("espresso")!;

        (espresso.YieldG / espresso.DoseG).ShouldBe(2m);
        espresso.TimeS!.Value.ShouldBeInRange(25, 30);
    }

    [Fact]
    public void Hario_switch_default_steeps_with_the_valve_closed_then_drains()
    {
        var method = BrewMethodCatalog.Default.Get("hario-switch");
        var recipe = DefaultRecipeCatalog.Default.ForMethod("hario-switch")!;

        method.Name.ShouldBe("Hario Switch");
        method.Uses(BrewParameter.Steps).ShouldBeTrue();
        method.Uses(BrewParameter.Filter).ShouldBeTrue();
        recipe.Steps!.First().Kind.ShouldBe(RecipeStepKind.Pour);
        recipe.Steps!.Last().Kind.ShouldBe(RecipeStepKind.Wait);
        recipe.Steps!.Last().Note.ShouldBe("Open the valve");
        recipe.TimeS.ShouldBe(recipe.Steps!.Last().AtS + recipe.Steps!.Last().DurationS);
    }

    [Fact]
    public void Every_default_has_a_dose_and_liquid_and_only_what_its_method_uses()
    {
        foreach (var recipe in DefaultRecipeCatalog.Default.Recipes)
        {
            var method = BrewMethodCatalog.Default.Get(recipe.MethodCode);
            recipe.DoseG.ShouldNotBeNull(recipe.Name);
            (recipe.WaterG ?? recipe.YieldG).ShouldNotBeNull(recipe.Name);
            if (method.Uses(BrewParameter.Steps) && recipe.Steps is not null)
            {
                recipe.Steps.Select(s => s.AtS).ShouldBe(recipe.Steps.Select(s => s.AtS).Order(), recipe.Name);
                recipe.Steps.Where(s => s.WaterG is not null).Select(s => s.WaterG!.Value).ShouldBe(recipe.Steps.Where(s => s.WaterG is not null).Select(s => s.WaterG!.Value).Order(), recipe.Name);
                recipe.Steps.Last(s => s.WaterG is not null).WaterG.ShouldBe(recipe.WaterG, recipe.Name);
            }
        }
    }

    [Fact]
    public void ToRecipe_copies_the_template_as_an_editable_personal_recipe()
    {
        var template = DefaultRecipeCatalog.Default.ForMethod("v60")!;

        var recipe = template.ToRecipe("g1", "b1");

        recipe.MethodCode.ShouldBe("v60");
        recipe.Name.ShouldBe(template.Name);
        recipe.DoseG.ShouldBe(template.DoseG);
        recipe.Steps.ShouldBe(template.Steps!);
        recipe.GrinderId.ShouldBe("g1");
        recipe.BrewerId.ShouldBe("b1");
        recipe.IsDefaultTemplate.ShouldBeFalse();
        recipe.Ratio.ShouldBe(16m);
    }

    [Fact]
    public void Parse_rejects_bad_data()
    {
        Should.Throw<JsonException>(() => Parse("""{ "version": 1, "recipes": [] }"""));
        Should.Throw<JsonException>(() => Parse("""{ "version": 1, "recipes": [ { "methodCode": "nope", "name": "x" } ] }"""));
        Should.Throw<JsonException>(() => Parse("""{ "version": 1, "recipes": [ { "methodCode": "espresso", "name": "a" }, { "methodCode": "espresso", "name": "b" } ] }"""));
        Should.Throw<JsonException>(() => Parse("""{ "version": 1, "recipes": [ { "methodCode": "moka-pot", "name": "x", "tempC": 90 } ] }"""));
        Should.Throw<JsonException>(() => Parse("""{ "version": 1, "recipes": [ { "methodCode": "espresso", "name": "x", "surprise": 1 } ] }"""));
    }

    [Fact]
    public void Pour_routines_load_with_several_per_method_and_a_pattern_on_the_pours()
    {
        var routines = DefaultRecipeCatalog.Routines.Recipes;

        routines.Select(r => r.Name).ShouldContain("4:6 method (Tetsu Kasuya)");
        routines.Count(r => r.MethodCode == "v60").ShouldBe(3);
        var fourSix = routines.Single(r => r.Name.StartsWith("4:6", StringComparison.Ordinal));
        fourSix.Steps!.Where(s => s.Kind == RecipeStepKind.Pour).Select(s => s.WaterG).ShouldBe([60m, 120m, 180m, 240m, 300m]);
        fourSix.Steps!.Where(s => s.Kind == RecipeStepKind.Pour).ShouldAllBe(s => s.Pattern != null);
    }

    [Fact]
    public void Every_routine_ends_with_its_total_water_and_keeps_its_steps_in_order()
    {
        foreach (var routine in DefaultRecipeCatalog.Routines.Recipes.Where(r => r.Steps is { Count: > 0 }))
        {
            routine.Steps!.Select(s => s.AtS).ShouldBe(routine.Steps!.Select(s => s.AtS).Order(), routine.Name);
            routine.Steps!.Where(s => s.WaterG is not null).Max(s => s.WaterG).ShouldBe(routine.WaterG, routine.Name);
        }
    }

    [Fact]
    public void A_routine_copied_as_a_recipe_keeps_its_steps_and_patterns()
    {
        var recipe = DefaultRecipeCatalog.Routines.Recipes.Single(r => r.Name.StartsWith("Ultimate", StringComparison.Ordinal)).ToRecipe();

        recipe.Steps.First().Pattern.ShouldBe(PourPattern.Circles);
        recipe.IsDefaultTemplate.ShouldBeFalse();
    }
}
