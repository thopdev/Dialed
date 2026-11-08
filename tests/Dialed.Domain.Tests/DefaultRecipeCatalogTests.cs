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
}
