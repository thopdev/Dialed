using Dialed.Domain.BrewMethods;
using Dialed.Domain.DialIn;
using Dialed.Domain.Equipment;
using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class DialInAdvisorTests
{
    private static readonly BrewMethod Espresso = BrewMethodCatalog.Default.Get("espresso");
    private static readonly BrewMethod V60 = BrewMethodCatalog.Default.Get("v60");
    private static readonly GrindScale Clicks = GrindScale.Clicks(40);

    private static Brew EspressoShot(Taste taste, int? timeS = 28, decimal? grind = 12m, decimal? temp = 93m) =>
        new() { BagId = "b", MethodCode = "espresso", DoseG = 18m, YieldG = 36m, GrindSetting = grind, TempC = temp, TimeS = timeS, Taste = taste };

    private static Brew PourOver(Taste taste, decimal? grind = 20m, decimal? temp = 94m) =>
        new() { BagId = "b", MethodCode = "v60", DoseG = 15m, WaterG = 250m, GrindSetting = grind, TempC = temp, TimeS = 180, Taste = taste };

    [Fact]
    public void A_balanced_cup_gets_keep_and_no_second_suggestion()
    {
        var advice = DialInAdvisor.Advise(Espresso, EspressoShot(new Taste()), Clicks);

        advice.Primary.Kind.ShouldBe(SuggestionKind.Keep);
        advice.Primary.Reason.ShouldBe(SuggestionReason.Balanced);
        advice.Secondary.ShouldBeNull();
    }

    [Fact]
    public void Sour_espresso_goes_finer_by_one_click_and_a_degree_hotter()
    {
        var advice = DialInAdvisor.Advise(Espresso, EspressoShot(new Taste(Acidity: -2), timeS: 20), Clicks);

        advice.Primary.ShouldBe(new DialInSuggestion(SuggestionKind.GrindFiner, SuggestionReason.Sour, 12m, 11m));
        advice.Secondary.ShouldBe(new DialInSuggestion(SuggestionKind.HotterWater, SuggestionReason.Sour, 93m, 94m));
    }

    [Fact]
    public void Bitter_espresso_goes_coarser_and_cooler()
    {
        var advice = DialInAdvisor.Advise(Espresso, EspressoShot(new Taste(Acidity: 1), timeS: 40), Clicks);

        advice.Primary.ShouldBe(new DialInSuggestion(SuggestionKind.GrindCoarser, SuggestionReason.Bitter, 12m, 13m));
        advice.Secondary.ShouldBe(new DialInSuggestion(SuggestionKind.CoolerWater, SuggestionReason.Bitter, 93m, 92m));
    }

    [Fact]
    public void Weak_espresso_gets_more_dose_then_a_shorter_ratio()
    {
        var advice = DialInAdvisor.Advise(Espresso, EspressoShot(new Taste(Strength: -1)), Clicks);

        advice.Primary.ShouldBe(new DialInSuggestion(SuggestionKind.IncreaseDose, SuggestionReason.Weak, 18m, 18.5m));
        advice.Secondary.ShouldBe(new DialInSuggestion(SuggestionKind.ShorterRatio, SuggestionReason.Weak, 36m, 34m));
    }

    [Fact]
    public void Strong_espresso_gets_a_longer_ratio_then_less_dose()
    {
        var advice = DialInAdvisor.Advise(Espresso, EspressoShot(new Taste(Strength: 2)), Clicks);

        advice.Primary.ShouldBe(new DialInSuggestion(SuggestionKind.LongerRatio, SuggestionReason.Strong, 36m, 38m));
        advice.Secondary.ShouldBe(new DialInSuggestion(SuggestionKind.DecreaseDose, SuggestionReason.Strong, 18m, 17.5m));
    }

    [Fact]
    public void Sour_wins_over_weak_and_the_weak_fix_becomes_the_second_suggestion_only_when_the_taste_rules_run_out()
    {
        var advice = DialInAdvisor.Advise(Espresso, EspressoShot(new Taste(Acidity: -1, Strength: -1)), Clicks);

        advice.Primary.Kind.ShouldBe(SuggestionKind.GrindFiner);
        advice.Secondary!.Kind.ShouldBe(SuggestionKind.HotterWater); // two suggestions only: the first two distinct fixes by priority
    }

    [Fact]
    public void A_fast_shot_that_tastes_fine_is_still_ground_finer()
    {
        var advice = DialInAdvisor.Advise(Espresso, EspressoShot(new Taste(), timeS: 18), Clicks);

        advice.Primary.ShouldBe(new DialInSuggestion(SuggestionKind.GrindFiner, SuggestionReason.ShotTooFast, 12m, 11m));
    }

    [Fact]
    public void A_slow_shot_that_tastes_fine_is_still_ground_coarser()
    {
        var advice = DialInAdvisor.Advise(Espresso, EspressoShot(new Taste(), timeS: 45), Clicks);

        advice.Primary.ShouldBe(new DialInSuggestion(SuggestionKind.GrindCoarser, SuggestionReason.ShotTooSlow, 12m, 13m));
    }

    [Fact]
    public void Pour_over_ignores_shot_time_and_uses_bigger_steps()
    {
        var advice = DialInAdvisor.Advise(V60, PourOver(new Taste(Acidity: -1)), Clicks);

        advice.Primary.ShouldBe(new DialInSuggestion(SuggestionKind.GrindFiner, SuggestionReason.Sour, 20m, 19m));
        advice.Secondary.ShouldBe(new DialInSuggestion(SuggestionKind.HotterWater, SuggestionReason.Sour, 94m, 96m));
    }

    [Fact]
    public void Weak_pour_over_uses_a_whole_gram_and_five_percent_less_water()
    {
        var advice = DialInAdvisor.Advise(V60, PourOver(new Taste(Strength: -1)), Clicks);

        advice.Primary.ShouldBe(new DialInSuggestion(SuggestionKind.IncreaseDose, SuggestionReason.Weak, 15m, 16m));
        advice.Secondary.ShouldBe(new DialInSuggestion(SuggestionKind.ShorterRatio, SuggestionReason.Weak, 250m, 235m));
    }

    [Fact]
    public void Temperature_stays_inside_80_to_100()
    {
        var advice = DialInAdvisor.Advise(V60, PourOver(new Taste(Acidity: -1), temp: 99m), Clicks);

        advice.Secondary.ShouldBe(new DialInSuggestion(SuggestionKind.HotterWater, SuggestionReason.Sour, 99m, 100m));
        DialInAdvisor.Advise(V60, PourOver(new Taste(Acidity: -1), temp: 100m), Clicks).Secondary.ShouldBeNull();
    }

    [Fact]
    public void The_grind_step_respects_the_grinder_scale()
    {
        var coarseDial = GrindScale.Number(1m, 11m, 0.5m);
        var stepless = GrindScale.Number(0m, 50m);

        DialInAdvisor.Advise(V60, PourOver(new Taste(Acidity: -1), grind: 5m), coarseDial).Primary.To.ShouldBe(4.5m);
        DialInAdvisor.Advise(V60, PourOver(new Taste(Acidity: 1), grind: 5m), coarseDial).Primary.To.ShouldBe(5.5m);
        DialInAdvisor.Advise(V60, PourOver(new Taste(Acidity: -1), grind: 20m), stepless).Primary.To.ShouldBe(19m); // 2% of the range
    }

    [Fact]
    public void At_the_end_of_the_scale_the_grind_suggestion_is_skipped_for_the_next_one()
    {
        var advice = DialInAdvisor.Advise(V60, PourOver(new Taste(Acidity: -1), grind: 0m), Clicks);

        advice.Primary.Kind.ShouldBe(SuggestionKind.HotterWater);
        advice.Secondary.ShouldBeNull();
    }

    [Fact]
    public void Without_a_grinder_scale_or_values_the_direction_is_still_given()
    {
        var advice = DialInAdvisor.Advise(V60, PourOver(new Taste(Acidity: 2), grind: null, temp: null), grindScale: null);

        advice.Primary.ShouldBe(new DialInSuggestion(SuggestionKind.GrindCoarser, SuggestionReason.Bitter));
        advice.Secondary.ShouldBe(new DialInSuggestion(SuggestionKind.CoolerWater, SuggestionReason.Bitter));
    }

    [Fact]
    public void Each_kind_appears_once_even_when_several_rules_want_it()
    {
        var advice = DialInAdvisor.Advise(Espresso, EspressoShot(new Taste(Acidity: -1, Sweetness: -1), timeS: 15), Clicks);

        new[] { advice.Primary.Kind, advice.Secondary!.Kind }.Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public void The_same_brew_always_gets_the_same_advice()
    {
        var brew = EspressoShot(new Taste(Acidity: -1, Strength: 1, Body: -1), timeS: 22);

        DialInAdvisor.Advise(Espresso, brew, Clicks).ShouldBe(DialInAdvisor.Advise(Espresso, brew, Clicks));
    }
}
