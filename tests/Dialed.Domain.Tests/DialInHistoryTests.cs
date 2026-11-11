using Dialed.Domain.BrewMethods;
using Dialed.Domain.DialIn;
using Dialed.Domain.Model;

namespace Dialed.Domain.Tests;

public sealed class DialInHistoryTests
{
    private static BrewMethod Method(string code) => BrewMethodCatalog.Default.Find(code)!;

    private static Brew Shot(decimal grind = 12m, int? rating = 2) =>
        new() { BagId = "b", MethodCode = "espresso", DoseG = 18m, YieldG = 36m, GrindSetting = grind, TempC = 93m, Rating = rating };

    [Theory]
    [InlineData(2, 0, true)]
    [InlineData(3, 0, true)]
    [InlineData(4, 0, false)]
    [InlineData(5, -1, true)]
    public void Advice_is_offered_for_a_low_rating_or_an_unbalanced_taste(int rating, int acidity, bool expected) =>
        DialInHistory.NeedsAdvice(Shot(rating: rating) with { Taste = new Taste(Acidity: acidity) }).ShouldBe(expected);

    [Fact]
    public void An_unrated_balanced_brew_needs_no_advice() =>
        DialInHistory.NeedsAdvice(Shot(rating: null)).ShouldBeFalse();

    [Fact]
    public void Grinding_finer_without_a_better_rating_counts_as_already_tried()
    {
        DialInHistory.AlreadyTried(SuggestionKind.GrindFiner, Method("espresso"), Shot(grind: 11m, rating: 2), Shot(grind: 12m, rating: 2)).ShouldBeTrue();
        DialInHistory.AlreadyTried(SuggestionKind.GrindCoarser, Method("espresso"), Shot(grind: 11m, rating: 2), Shot(grind: 12m, rating: 2)).ShouldBeFalse();
    }

    [Fact]
    public void A_change_that_improved_the_rating_or_an_unrated_brew_does_not_count()
    {
        DialInHistory.AlreadyTried(SuggestionKind.GrindFiner, Method("espresso"), Shot(grind: 11m, rating: 3), Shot(grind: 12m, rating: 2)).ShouldBeFalse();
        DialInHistory.AlreadyTried(SuggestionKind.GrindFiner, Method("espresso"), Shot(grind: 11m, rating: null), Shot(grind: 12m, rating: 2)).ShouldBeFalse();
    }

    [Fact]
    public void Ratio_changes_compare_yield_for_espresso_and_water_for_filter()
    {
        DialInHistory.AlreadyTried(SuggestionKind.LongerRatio, Method("espresso"), Shot() with { YieldG = 40m }, Shot()).ShouldBeTrue();

        var before = new Brew { BagId = "b", MethodCode = "v60", DoseG = 15m, WaterG = 250m, Rating = 3 };
        DialInHistory.AlreadyTried(SuggestionKind.ShorterRatio, Method("v60"), before with { WaterG = 230m }, before).ShouldBeTrue();
        DialInHistory.AlreadyTried(SuggestionKind.HotterWater, Method("v60"), before with { TempC = 94m }, before).ShouldBeFalse();
    }
}
