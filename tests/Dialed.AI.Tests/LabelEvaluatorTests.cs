using System.Security.Cryptography;
using System.Text.Json;
using Dialed.AI.Labels;
using Dialed.AI.Labels.Evaluation;

namespace Dialed.AI.Tests;

/// <summary>The CI mode of the harness: runs the committed golden set through the fake and through test doubles with known answers.</summary>
public class LabelEvaluatorTests
{
    private static readonly GoldenSet Golden = GoldenSet.Load(GoldenSetTests.GoldenFolder);

    [Fact]
    public async Task An_extractor_that_reads_every_label_right_scores_100_percent()
    {
        var report = await new LabelEvaluator(new AnswerKeyExtractor(Golden)).RunAsync(Golden, cancellationToken: TestContext.Current.CancellationToken);

        report.Accuracy.ShouldBe(1);
        report.OnLabelAccuracy.ShouldBe(1);
        report.Errors.ShouldBe(0);
        report.Cases.Count.ShouldBe(Golden.Cases.Count);
        report.Fields.ShouldAllBe(f => f.Accuracy == 1);
        report.ModelIds.ShouldBe(["answer-key"]);
        report.InputTokens.ShouldBe(100 * Golden.Cases.Count);
        report.MeanConfidence.Correct.ShouldBe(0.9, 1e-9);
    }

    [Fact]
    public async Task Answers_in_other_spellings_still_score_100_percent()
    {
        var report = await new LabelEvaluator(new AnswerKeyExtractor(Golden, Respell)).RunAsync(Golden, cancellationToken: TestContext.Current.CancellationToken);

        var misses = report.Cases.SelectMany(c => c.Fields.Where(f => !f.IsCorrect).Select(f => $"{c.Id}.{f.Field}: {f.Expected} vs {f.Actual}"));
        misses.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_fake_extractor_gets_its_own_label_right_and_the_rest_partly()
    {
        var progress = new List<CaseResult>();
        var report = await new LabelEvaluator(new FakeLabelExtractor()).RunAsync(Golden, new Collect(progress), TestContext.Current.CancellationToken);

        progress.Count.ShouldBe(Golden.Cases.Count);
        report.Cases.Single(c => c.Id == "synthetic-fake-match").Accuracy.ShouldBe(1);
        report.Accuracy.ShouldBeInRange(0.1, 0.9);
        report.PromptVersion.ShouldBe(LabelPrompt.Version);
        report.ModelIds.ShouldBe([FakeLabelExtractor.ModelId]);

        // The fake always says Ethiopia: right on two labels, wrong on others, a guess on the blend.
        var country = report.Fields.Single(f => f.Field == "country");
        country.Matched.ShouldBe(2);
        country.Unexpected.ShouldBe(1);
        country.Wrong.ShouldBe(Golden.Cases.Count(c => c.ExpectedValue("country") is { } value && value != "Ethiopia"));
        report.Fields.Single(f => f.Field == "farm").Missed.ShouldBe(Golden.Cases.Count(c => c.ExpectedValue("farm") != null));
    }

    [Fact]
    public async Task A_failed_extraction_counts_as_an_empty_draft()
    {
        var report = await new LabelEvaluator(new FailingExtractor()).RunAsync(Golden, cancellationToken: TestContext.Current.CancellationToken);

        report.Errors.ShouldBe(Golden.Cases.Count);
        report.OnLabelAccuracy.ShouldBe(0);
        report.Accuracy.ShouldBeGreaterThan(0, "fields that are not on the label are still right when left out");
        report.PromptVersion.ShouldBe(LabelPrompt.Version);
        report.ModelIds.ShouldBeEmpty();
        report.ToMarkdown().ShouldContain("| error |");
    }

    [Fact]
    public async Task The_markdown_summary_has_the_totals_the_table_and_the_misses()
    {
        var report = await new LabelEvaluator(new FakeLabelExtractor()).RunAsync(Golden, cancellationToken: TestContext.Current.CancellationToken);

        var markdown = report.ToMarkdown(target: 0.8);

        markdown.ShouldContain("## Label extraction: golden set");
        markdown.ShouldContain("below target 80.0%");
        markdown.ShouldContain($"Prompt `{LabelPrompt.Version}` · model `fake`");
        markdown.ShouldContain("| tastingNotes |");
        markdown.ShouldContain("| synthetic-minimal-blend | country | – | Ethiopia | unexpected |");
        report.ToMarkdown().ShouldNotContain("target");
    }

    [Fact]
    public async Task A_perfect_run_says_it_meets_the_target_and_lists_no_misses()
    {
        var report = await new LabelEvaluator(new AnswerKeyExtractor(Golden)).RunAsync(Golden, cancellationToken: TestContext.Current.CancellationToken);

        var markdown = report.ToMarkdown(target: 0.8);

        markdown.ShouldContain("✅ target 80.0%");
        markdown.ShouldNotContain("<details>");
    }

    [Fact]
    public async Task The_json_report_has_every_field_result()
    {
        var report = await new LabelEvaluator(new FakeLabelExtractor()).RunAsync(Golden, cancellationToken: TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(report.ToJson());

        json.RootElement.GetProperty("promptVersion").GetString().ShouldBe(LabelPrompt.Version);
        json.RootElement.GetProperty("accuracy").GetDouble().ShouldBe(Math.Round(report.Accuracy, 4));
        json.RootElement.GetProperty("fields").GetArrayLength().ShouldBe(BeanDraft.FieldNames.Length);
        var first = json.RootElement.GetProperty("cases")[0];
        first.GetProperty("fields").GetArrayLength().ShouldBe(BeanDraft.FieldNames.Length);
        first.GetProperty("fields")[0].GetProperty("outcome").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void An_empty_report_has_zero_accuracy()
    {
        var report = new EvaluationReport("v", [], TimeSpan.Zero, DateTimeOffset.UnixEpoch);

        report.Accuracy.ShouldBe(0);
        report.OnLabelAccuracy.ShouldBe(0);
        report.MeanConfidence.ShouldBe((0, 0));
        new CaseResult("x", [], null, null, 0, 0, TimeSpan.Zero).Accuracy.ShouldBe(0);
    }

    /// <summary>Writes expected values the way a model might: other case, units, date formats, separators.</summary>
    private static string Respell(string field, string value) => field switch
    {
        "name" or "roaster" => value.ToUpperInvariant(),
        "process" => value + " process",
        "roastLevel" => value.Replace('-', ' ') + " roast",
        "varieties" or "tastingNotes" => string.Join(" / ", value.Split(", ").Reverse()),
        "roastDate" => DateOnly.ParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture),
        "weightG" => value + " g",
        _ => value,
    };

    /// <summary>Answers with the expected values of the golden case whose image it is given.</summary>
    private sealed class AnswerKeyExtractor(GoldenSet set, Func<string, string, string>? respell = null) : ILabelExtractor
    {
        private readonly Dictionary<string, GoldenCase> _byImage = set.Cases.ToDictionary(c => Hash(File.ReadAllBytes(c.ImagePath)));

        public Task<LabelExtraction> ExtractAsync(byte[] jpeg, CancellationToken cancellationToken = default)
        {
            var item = _byImage[Hash(jpeg)];
            var fields = item.Expected
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key, pair => new DraftField(respell is null ? pair.Value : respell(pair.Key, pair.Value!), 0.9));
            return Task.FromResult(new LabelExtraction(BeanDraft.FromFields(fields), LabelPrompt.Version, "answer-key", 100, 50));
        }

        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    }

    private sealed class FailingExtractor : ILabelExtractor
    {
        public Task<LabelExtraction> ExtractAsync(byte[] jpeg, CancellationToken cancellationToken = default) =>
            throw new LabelExtractionException("The model answered 503.");
    }

    private sealed class Collect(List<CaseResult> results) : IProgress<CaseResult>
    {
        public void Report(CaseResult value) => results.Add(value);
    }
}
