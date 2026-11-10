using Bunit;
using Dialed.Client.Data;
using Dialed.Domain.DialIn;
using Dialed.Domain.Equipment;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Microsoft.Extensions.DependencyInjection;

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

    private IRepository<DialInFeedback> Feedback => Services.GetRequiredService<IRepository<DialInFeedback>>();

    [Fact]
    public async Task Applying_a_suggestion_is_recorded_with_the_rules_version_and_opens_the_next_brew_with_it()
    {
        var brew = Shot(new Taste(Acidity: -2));
        var card = Card(brew, GrindScale.Clicks(40));

        card.Find("#dialin-apply-primary").GetAttribute("aria-label").ShouldBe("Apply to next brew: Grind finer");
        card.Find("#dialin-apply-primary").Click();

        var saved = (await Feedback.ListAsync(Xunit.TestContext.Current.CancellationToken)).Single();
        saved.BrewId.ShouldBe(brew.Id);
        saved.Kind.ShouldBe(SuggestionKind.GrindFiner);
        saved.Outcome.ShouldBe(DialInOutcome.Applied);
        saved.PromptVersion.ShouldBe(DialInAdvisor.RulesVersion);
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri.ShouldEndWith($"brew/b/log/again/{brew.Id}?apply=primary");
    }

    [Fact]
    public async Task Dismissing_hides_the_advice_and_stays_hidden_next_time()
    {
        var brew = Shot(new Taste(Acidity: -2));
        var card = Card(brew, GrindScale.Clicks(40));

        card.Find("#dialin-dismiss").Click();

        card.FindAll("#dialin-advice").ShouldBeEmpty();
        card.Find("#dialin-dismissed").TextContent.ShouldContain("hidden");
        (await Feedback.ListAsync(Xunit.TestContext.Current.CancellationToken)).Single().Outcome.ShouldBe(DialInOutcome.Dismissed);
        Card(brew, GrindScale.Clicks(40)).FindAll("#dialin-advice").ShouldBeEmpty();
    }

    [Fact]
    public void A_balanced_cup_offers_nothing_to_apply_or_dismiss()
    {
        var card = Card(Shot(new Taste()));

        card.FindAll("#dialin-apply-primary, #dialin-dismiss").ShouldBeEmpty();
    }

    [Fact]
    public void Without_a_target_value_there_is_no_apply_button()
    {
        var card = Card(Shot(new Taste(Acidity: 1), grind: null));

        card.FindAll("#dialin-apply-primary").ShouldBeEmpty();
    }
}
