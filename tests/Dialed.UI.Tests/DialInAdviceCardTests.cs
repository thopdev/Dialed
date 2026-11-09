using Bunit;
using Dialed.Domain.Equipment;
using Dialed.Domain.Model;
using Dialed.UI.Components;

namespace Dialed.UI.Tests;

public sealed class DialInAdviceCardTests : UiTestContext
{
    private static Brew Shot(Taste taste, decimal? grind = 12m) =>
        new() { BagId = "b", MethodCode = "espresso", DoseG = 18m, YieldG = 36m, GrindSetting = grind, TempC = 93m, TimeS = 28, Taste = taste };

    private IRenderedComponent<DialInAdviceCard> Card(Brew brew, GrindScale? scale = null) =>
        Render<DialInAdviceCard>(p => p.Add(x => x.Brew, brew).Add(x => x.Scale, scale));

    [Fact]
    public void Sour_espresso_shows_a_finer_grind_first_and_hotter_water_second_with_values()
    {
        var card = Card(Shot(new Taste(Acidity: -2)), GrindScale.Clicks(40));

        card.Find("#dialin-primary").TextContent.ShouldContain("Grind finer (12 → 11)");
        card.Find("#dialin-primary").TextContent.ShouldContain("sour");
        card.Find("#dialin-secondary").TextContent.ShouldContain("Use hotter water (93 °C → 94 °C)");
    }

    [Fact]
    public void Without_a_grinder_scale_the_direction_is_shown_without_numbers()
    {
        var card = Card(Shot(new Taste(Acidity: 1), grind: null));

        card.Find("#dialin-primary").TextContent.ShouldContain("Grind coarser");
        card.Find("#dialin-primary").TextContent.ShouldNotContain("→");
    }

    [Fact]
    public void A_balanced_cup_says_to_keep_the_recipe_and_has_no_second_suggestion()
    {
        var card = Card(Shot(new Taste()));

        card.Find("#dialin-primary").TextContent.ShouldContain("Keep this recipe");
        card.FindAll("#dialin-secondary").ShouldBeEmpty();
    }

    [Fact]
    public void An_unknown_method_shows_nothing()
    {
        var card = Card(Shot(new Taste(Acidity: -1)) with { MethodCode = "made-up" });

        card.FindAll("#dialin-advice").ShouldBeEmpty();
    }
}
