using Dialed.Domain.Text;

namespace Dialed.Domain.Tests;

public sealed class FuzzyMatchTests
{
    [Theory]
    [InlineData("Café  Noir!", "cafe noir")]
    [InlineData("  Friedhats ", "friedhats")]
    [InlineData("Tim Wendelboe", "tim wendelboe")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("---", "")]
    public void Normalize_removes_case_accents_and_punctuation(string? input, string expected)
    {
        FuzzyMatch.Normalize(input).ShouldBe(expected);
    }

    [Fact]
    public void Scores_rank_exact_above_prefix_above_word_above_contains_above_typo()
    {
        var exact = FuzzyMatch.Score("Friedhats", "friedhats");
        var prefix = FuzzyMatch.Score("fried", "Friedhats Coffee");
        var word = FuzzyMatch.Score("coffee", "Friedhats Coffee");
        var contains = FuzzyMatch.Score("hats", "Friedhats Coffee");
        var typo = FuzzyMatch.Score("freidhats", "Friedhats Coffee");

        exact.ShouldBeGreaterThan(prefix);
        prefix.ShouldBeGreaterThan(word);
        word.ShouldBeGreaterThan(contains);
        contains.ShouldBeGreaterThan(typo);
        typo.ShouldBeGreaterThan(0d);
    }

    [Theory]
    [InlineData("xyz", "Friedhats")]
    [InlineData("", "Friedhats")]
    [InlineData("fried", "")]
    [InlineData(null, "Friedhats")]
    [InlineData("abc", "abd")]
    public void Unrelated_or_empty_input_scores_zero(string? query, string? candidate)
    {
        FuzzyMatch.Score(query, candidate).ShouldBe(0d);
    }

    [Fact]
    public void Rank_returns_matches_best_first_and_keeps_order_for_ties()
    {
        string[] names = ["Coffee Collective", "Friedhats", "The Coffee Lab", "Fried Coffee", "Other"];

        var ranked = FuzzyMatch.Rank(names, "coffee", name => name);

        ranked.ShouldBe(["Coffee Collective", "The Coffee Lab", "Fried Coffee"]);
    }

    [Fact]
    public void Rank_finds_typos_and_skips_non_matches()
    {
        string[] names = ["Friedhats", "Tim Wendelboe", "Onyx"];

        FuzzyMatch.Rank(names, "wendelbo", name => name).ShouldBe(["Tim Wendelboe"]);
        FuzzyMatch.Rank(names, "freidhats", name => name).ShouldBe(["Friedhats"]);
        FuzzyMatch.Rank(names, "", name => name).ShouldBeEmpty();
    }

    [Fact]
    public void Rank_rejects_null_arguments()
    {
        Should.Throw<ArgumentNullException>(() => FuzzyMatch.Rank<string>(null!, "a", n => n));
        Should.Throw<ArgumentNullException>(() => FuzzyMatch.Rank(["a"], "a", null!));
    }
}
