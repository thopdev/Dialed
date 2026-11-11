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
        var brew = Shot(new Taste(Acidity: -2)) with { Rating = 2 };
        var card = Card(brew, GrindScale.Clicks(40));

        card.Find("#dialin-apply-primary").GetAttribute("aria-label").ShouldBe("Apply to next brew: Grind finer");
        card.Find("#dialin-apply-primary").Click();

        var saved = (await Feedback.ListAsync(Xunit.TestContext.Current.CancellationToken)).Single();
        saved.BrewId.ShouldBe(brew.Id);
        saved.Kind.ShouldBe(SuggestionKind.GrindFiner);
        saved.Outcome.ShouldBe(DialInOutcome.Applied);
        saved.PromptVersion.ShouldBe(DialInAdvisor.RulesVersion);
        saved.RatingBefore.ShouldBe(brew.Rating);
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
    public async Task Asking_the_assistant_sends_the_last_brews_of_the_bag_and_shows_the_changes_as_from_to()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var bags = Services.GetRequiredService<IRepository<Bag>>();
        var beans = Services.GetRequiredService<IRepository<Bean>>();
        var brews = Services.GetRequiredService<IRepository<Brew>>();
        var bean = await beans.SaveAsync(new Bean { Name = "Gesha", Origin = new BeanOrigin("Panama"), Process = "Washed", RoastLevel = RoastLevel.Light }, ct);
        var bag = await bags.SaveAsync(new Bag { BeanId = bean.Id, WeightG = 250m }, ct);
        var now = DateTimeOffset.UtcNow;
        await brews.SaveAsync(Shot(new Taste(Acidity: -2), grind: 13m) with { BagId = bag.Id, BrewedAt = now.AddDays(-1), Rating = 2 }, ct);
        await brews.SaveAsync(Shot(new Taste(Acidity: -1), grind: 14m) with { BagId = bag.Id, BrewedAt = now.AddDays(-2) }, ct);
        await brews.SaveAsync(Shot(new Taste()) with { BagId = "other", BrewedAt = now.AddDays(-1) }, ct);
        var brew = Shot(new Taste(Acidity: -2)) with { BagId = bag.Id, BrewedAt = now, Rating = 2 };
        var card = Card(brew, GrindScale.Clicks(40));

        card.Find("#dialin-ask").Click();

        var request = DialInAssistant.Requests.Single();
        request.MethodCode.ShouldBe("espresso");
        request.Brew.Taste.Acidity.ShouldBe(-2);
        request.Recent!.Select(r => r.GrindSetting).ShouldBe([13m, 14m]);
        request.BeanOrigin.ShouldBe("Panama");
        request.RoastLevel.ShouldBe("Light");
        request.GrindMax.ShouldBe(40m);
        card.Find("#dialin-ai-0").TextContent.ShouldContain("Grind finer (12 → 11)");
        card.Find("#dialin-ai-1").TextContent.ShouldContain("Use hotter water (93 °C → 94 °C)");
        card.Find("#dialin-ai-why").TextContent.ShouldContain("sour");
        card.FindAll("#dialin-ask").ShouldBeEmpty();
    }

    [Fact]
    public async Task Applying_the_assistants_change_is_recorded_as_ai_with_its_prompt_version()
    {
        var brew = Shot(new Taste(Acidity: -2)) with { Rating = 2 };
        var card = Card(brew, GrindScale.Clicks(40));
        card.Find("#dialin-ask").Click();

        card.Find("#dialin-ai-apply-1").Click();

        var saved = (await Feedback.ListAsync(Xunit.TestContext.Current.CancellationToken)).Single();
        saved.Source.ShouldBe("ai");
        saved.PromptVersion.ShouldBe("rules-1+recent");
        saved.Kind.ShouldBe(SuggestionKind.HotterWater);
        saved.RatingBefore.ShouldBe(2);
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri.ShouldEndWith($"brew/b/log/again/{brew.Id}?apply=ai&kind=HotterWater&to=94");
    }

    [Fact]
    public async Task Dismissing_after_asking_records_the_assistants_answer()
    {
        var card = Card(Shot(new Taste(Acidity: -2)), GrindScale.Clicks(40));
        card.Find("#dialin-ask").Click();

        card.Find("#dialin-dismiss").Click();

        var saved = (await Feedback.ListAsync(Xunit.TestContext.Current.CancellationToken)).Single();
        saved.Outcome.ShouldBe(DialInOutcome.Dismissed);
        saved.Source.ShouldBe("ai");
        saved.PromptVersion.ShouldBe("rules-1+recent");
    }

    [Fact]
    public void A_model_answer_without_a_known_code_shows_its_own_words()
    {
        DialInAssistant.Result = DialInAssistant.Result with { ReasonCode = null, Why = "Grind a little finer; the last shot was close." };
        var card = Card(Shot(new Taste(Acidity: -2)), GrindScale.Clicks(40));

        card.Find("#dialin-ask").Click();

        card.Find("#dialin-ai-why").TextContent.ShouldBe("Grind a little finer; the last shot was close.");
    }

    [Theory]
    [InlineData(Dialed.Client.Recipes.AssistantProblem.QuotaUsed, "all suggestions for today")]
    [InlineData(Dialed.Client.Recipes.AssistantProblem.Offline, "offline")]
    [InlineData(Dialed.Client.Recipes.AssistantProblem.Disabled, "switched off")]
    public void When_the_assistant_cannot_answer_the_rules_advice_stays_and_the_reason_is_shown(Dialed.Client.Recipes.AssistantProblem problem, string text)
    {
        DialInAssistant.Problem = problem;
        var card = Card(Shot(new Taste(Acidity: -2)), GrindScale.Clicks(40));

        card.Find("#dialin-ask").Click();

        card.Find("#dialin-ask-error").TextContent.ShouldContain(text);
        card.Find("#dialin-primary").TextContent.ShouldContain("Grind finer");
        card.FindAll("#dialin-ai").ShouldBeEmpty();
    }

    [Fact]
    public void A_balanced_cup_does_not_offer_the_assistant() =>
        Card(Shot(new Taste())).FindAll("#dialin-ask").ShouldBeEmpty();

    [Fact]
    public void Without_a_target_value_there_is_no_apply_button()
    {
        var card = Card(Shot(new Taste(Acidity: 1), grind: null));

        card.FindAll("#dialin-apply-primary").ShouldBeEmpty();
    }
}
