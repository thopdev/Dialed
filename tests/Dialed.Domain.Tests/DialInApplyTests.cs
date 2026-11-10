using Dialed.Domain.BrewMethods;
using Dialed.Domain.DialIn;
using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class DialInApplyTests
{
    private static BrewMethod Method(string code) => BrewMethodCatalog.Default.Find(code)!;

    private static Brew Shot() => new() { BagId = "b", MethodCode = "espresso", DoseG = 18m, YieldG = 36m, GrindSetting = 12m, TempC = 93m };

    [Fact]
    public void A_grind_suggestion_sets_the_grind_setting_and_nothing_else()
    {
        var result = DialInApply.Apply(Shot(), Method("espresso"), new DialInSuggestion(SuggestionKind.GrindFiner, SuggestionReason.Sour, 12m, 11m));

        result.GrindSetting.ShouldBe(11m);
        result.DoseG.ShouldBe(18m);
        result.YieldG.ShouldBe(36m);
    }

    [Fact]
    public void Dose_and_temperature_suggestions_set_their_own_values()
    {
        DialInApply.Apply(Shot(), Method("espresso"), new DialInSuggestion(SuggestionKind.IncreaseDose, SuggestionReason.Weak, 18m, 18.5m)).DoseG.ShouldBe(18.5m);
        DialInApply.Apply(Shot(), Method("espresso"), new DialInSuggestion(SuggestionKind.HotterWater, SuggestionReason.Sour, 93m, 94m)).TempC.ShouldBe(94m);
    }

    [Fact]
    public void A_longer_ratio_moves_the_yield_for_espresso_and_the_water_for_filter()
    {
        DialInApply.Apply(Shot(), Method("espresso"), new DialInSuggestion(SuggestionKind.LongerRatio, SuggestionReason.Strong, 36m, 38m)).YieldG.ShouldBe(38m);

        var filter = new Brew { BagId = "b", MethodCode = "v60", DoseG = 15m, WaterG = 250m };
        var result = DialInApply.Apply(filter, Method("v60"), new DialInSuggestion(SuggestionKind.LongerRatio, SuggestionReason.Strong, 250m, 262.5m));
        result.WaterG.ShouldBe(262.5m);
        result.YieldG.ShouldBeNull();
    }

    [Fact]
    public void Keep_and_a_suggestion_without_a_target_change_nothing()
    {
        var brew = Shot();

        DialInApply.Apply(brew, Method("espresso"), new DialInSuggestion(SuggestionKind.Keep, SuggestionReason.Balanced)).ShouldBe(brew);
        DialInApply.Apply(brew, Method("espresso"), new DialInSuggestion(SuggestionKind.GrindFiner, SuggestionReason.Sour, 12m)).ShouldBe(brew);
        DialInApply.CanApply(new DialInSuggestion(SuggestionKind.GrindFiner, SuggestionReason.Sour, 12m)).ShouldBeFalse();
    }
}
