using Dialed.AI.Labels;
using Dialed.AI.Labels.Evaluation;

namespace Dialed.AI.Tests;

public class FieldMatcherTests
{
    private readonly FieldMatcher _matcher = new();

    private FieldScore Score(string field, string? expected, string? actual, double confidence = 0.9) =>
        _matcher.Score(field, expected, actual is null ? DraftField.None : new DraftField(actual, confidence));

    [Theory]
    [InlineData("roaster", "Northbound Coffee Co.", "northbound coffee co")]
    [InlineData("roaster", "Maple & Iron Roasting", "Maple and Iron Roasting")]
    [InlineData("region", "Tarrazú", "Tarrazu")]
    [InlineData("name", "La Esperanza", "La Esperanze")]
    [InlineData("country", "USA", "United States")]
    [InlineData("process", "Washed", "Fully washed")]
    [InlineData("process", "Natural", "natural process")]
    [InlineData("process", "Wet hulled", "Giling Basah")]
    [InlineData("roastLevel", "medium-light", "Medium Light roast")]
    [InlineData("roastLevel", "dark", "DARK")]
    [InlineData("roastDate", "2026-10-03", "2026-10-03")]
    [InlineData("roastDate", "2026-10-03", "03.10.2026")]
    [InlineData("roastDate", "2026-09-02", "2 september 2026")]
    [InlineData("roastDate", "2026-08-30", "30 Aug 2026")]
    [InlineData("roastDate", "2026-07-05", "Roasted on: 05/07/2026")]
    [InlineData("weightG", "340", "12 oz")]
    [InlineData("weightG", "1000", "1 kg")]
    [InlineData("weightG", "500", "0,5 kg")]
    [InlineData("weightG", "250", "250g")]
    [InlineData("weightG", "454", "1 lb")]
    [InlineData("varieties", "SL28, SL34, Ruiru 11", "SL28 / SL34 / Ruiru 11")]
    [InlineData("tastingNotes", "blackcurrant, grapefruit, molasses", "Molasses; Grapefruit and blackcurrant")]
    [InlineData("tastingNotes", "melkchocolade, hazelnoot, rode bes", "melkchocolade - hazelnoot en rode bes")]
    public void Normalised_spellings_match(string field, string expected, string actual)
    {
        var score = Score(field, expected, actual);

        score.Outcome.ShouldBe(FieldOutcome.Match, $"{expected} vs {actual}");
        score.Score.ShouldBe(1);
        score.IsCorrect.ShouldBeTrue();
    }

    [Theory]
    [InlineData("roaster", "Halfway House Roasters", "ACME")]
    [InlineData("name", "Cerro Alto", "Cerro Azul")]
    [InlineData("process", "Red Honey", "Honey")]
    [InlineData("process", "Washed", "Natural")]
    [InlineData("roastLevel", "medium", "medium-dark")]
    [InlineData("roastLevel", "light", "filter")]
    [InlineData("roastDate", "2026-10-03", "2026-03-10")]
    [InlineData("roastDate", "2026-10-03", "soon")]
    [InlineData("weightG", "250", "15 g")]
    [InlineData("weightG", "250", "a bag")]
    [InlineData("weightG", "250", "250 cups")]
    [InlineData("varieties", "Bourbon, Typica", "Geisha")]
    public void Different_values_are_wrong(string field, string expected, string actual)
    {
        var score = Score(field, expected, actual);

        score.Outcome.ShouldBe(FieldOutcome.Wrong, $"{expected} vs {actual}");
        score.Score.ShouldBe(0);
        score.IsCorrect.ShouldBeFalse();
    }

    [Fact]
    public void A_list_with_some_items_right_gets_partial_credit()
    {
        var score = Score("tastingNotes", "red apple, panela, orange zest", "red apple, caramel");

        score.Outcome.ShouldBe(FieldOutcome.Partial);
        score.Score.ShouldBe(2 * 0.5 * (1.0 / 3) / (0.5 + 1.0 / 3), 1e-9);
    }

    [Fact]
    public void Empty_and_missing_values_are_told_apart()
    {
        Score("farm", null, null).Outcome.ShouldBe(FieldOutcome.BothEmpty);
        Score("farm", "", "  ").Score.ShouldBe(1);
        Score("farm", "Finca X", null).Outcome.ShouldBe(FieldOutcome.Missed);
        Score("farm", null, "Finca X").Outcome.ShouldBe(FieldOutcome.Unexpected);
        Score("farm", null, "Finca X").Score.ShouldBe(0);
    }

    [Fact]
    public void Confidence_is_kept_only_for_values_that_were_given()
    {
        Score("name", "A", "A", 0.7).Confidence.ShouldBe(0.7);
        Score("name", "A", null, 0.7).Confidence.ShouldBe(0);
    }

    [Fact]
    public void Expected_values_that_cannot_be_read_never_match()
    {
        Score("roastDate", "14-09-2026", "2026-09-14").Outcome.ShouldBe(FieldOutcome.Wrong);
        Score("weightG", "0", "0 g").Outcome.ShouldBe(FieldOutcome.Wrong);
    }

    [Fact]
    public void Unknown_fields_are_rejected()
    {
        Should.Throw<ArgumentException>(() => Score("altitude", "1800", "1800 m"));
        Should.Throw<ArgumentException>(() => LabelEvaluator.Get(BeanDraft.Empty, "altitude"));
    }

    [Fact]
    public void The_matcher_knows_every_draft_field()
    {
        FieldMatcher.FieldNames.ShouldBe(BeanDraft.FieldNames, ignoreOrder: true);
    }

    [Fact]
    public void Thresholds_can_be_tightened()
    {
        var strict = new FieldMatcher { TextThreshold = 1, WeightTolerance = 0 };

        strict.Score("name", "La Esperanza", new DraftField("La Esperanze", 1)).Outcome.ShouldBe(FieldOutcome.Wrong);
        strict.Score("weightG", "340", new DraftField("12 oz", 1)).Outcome.ShouldBe(FieldOutcome.Wrong);
    }

    [Theory]
    [InlineData("  Ethiopië  ", "ethiopie")]
    [InlineData("Maple & Iron", "maple and iron")]
    [InlineData("DRC", "dr congo")]
    public void Normalize_folds_case_accents_punctuation_and_aliases(string text, string expected) =>
        FieldMatcher.Normalize(text).ShouldBe(expected);

    [Fact]
    public void Similarity_is_one_for_two_empty_texts() => FieldMatcher.Similarity("", " - ").ShouldBe(1);
}
