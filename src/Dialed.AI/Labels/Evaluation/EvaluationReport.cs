using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dialed.AI.Labels.Evaluation;

/// <summary>Accuracy of one field over all cases.</summary>
public sealed record FieldSummary(string Field, double Accuracy, int OnLabel, int Matched, int Partial, int Wrong, int Missed, int Unexpected);

/// <summary>
/// The outcome of a golden set run. <see cref="Accuracy"/> is the mean field score over every field of every case (fields that are
/// not on the label count when the extractor correctly leaves them out). <see cref="OnLabelAccuracy"/> only counts fields that are on the label.
/// </summary>
public sealed class EvaluationReport(string promptVersion, IReadOnlyList<CaseResult> cases, TimeSpan duration, DateTimeOffset finishedAt)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string PromptVersion { get; } = promptVersion;

    public IReadOnlyList<CaseResult> Cases { get; } = cases;

    public TimeSpan Duration { get; } = duration;

    public DateTimeOffset FinishedAt { get; } = finishedAt;

    public IReadOnlyList<string> ModelIds => Cases.Select(c => c.ModelId).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private IEnumerable<FieldScore> AllFields => Cases.SelectMany(c => c.Fields);

    public double Accuracy => AllFields.Any() ? AllFields.Average(f => f.Score) : 0;

    public double OnLabelAccuracy => AllFields.Where(f => f.IsOnLabel).Select(f => f.Score).DefaultIfEmpty(0).Average();

    public int Errors => Cases.Count(c => c.Error is not null);

    public int InputTokens => Cases.Sum(c => c.InputTokens);

    public int OutputTokens => Cases.Sum(c => c.OutputTokens);

    /// <summary>Mean confidence the extractor gave to values that turned out right, and to values that were wrong or not on the label. A well calibrated model has a clear gap.</summary>
    public (double Correct, double Incorrect) MeanConfidence
    {
        get
        {
            var withValue = AllFields.Where(f => f.Actual is not null).ToList();
            double Mean(IEnumerable<FieldScore> fields) => fields.Select(f => f.Confidence).DefaultIfEmpty(0).Average();
            return (Mean(withValue.Where(f => f.Outcome == FieldOutcome.Match)), Mean(withValue.Where(f => f.Outcome is FieldOutcome.Wrong or FieldOutcome.Unexpected)));
        }
    }

    public IReadOnlyList<FieldSummary> Fields => BeanDraft.FieldNames
        .Select(name =>
        {
            var scores = AllFields.Where(f => f.Field == name).ToList();
            int Count(FieldOutcome outcome) => scores.Count(f => f.Outcome == outcome);
            return new FieldSummary(
                name,
                scores.Count == 0 ? 0 : scores.Average(f => f.Score),
                scores.Count(f => f.IsOnLabel),
                Count(FieldOutcome.Match),
                Count(FieldOutcome.Partial),
                Count(FieldOutcome.Wrong),
                Count(FieldOutcome.Missed),
                Count(FieldOutcome.Unexpected));
        })
        .ToList();

    public string ToJson() => JsonSerializer.Serialize(
        new
        {
            PromptVersion,
            ModelIds,
            FinishedAt,
            DurationSeconds = Math.Round(Duration.TotalSeconds, 1),
            Accuracy = Math.Round(Accuracy, 4),
            OnLabelAccuracy = Math.Round(OnLabelAccuracy, 4),
            Errors,
            InputTokens,
            OutputTokens,
            Fields,
            Cases = Cases.Select(c => new { c.Id, Accuracy = Math.Round(c.Accuracy, 4), c.Error, c.ModelId, c.InputTokens, c.OutputTokens, c.Fields }),
        },
        Json);

    /// <summary>A Markdown summary for the GitHub job summary and the PR comment.</summary>
    public string ToMarkdown(double? target = null)
    {
        static string Percent(double value) => (value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

        var markdown = new StringBuilder();
        markdown.AppendLine("## Label extraction: golden set");
        markdown.AppendLine();
        var verdict = target is { } goal ? (Accuracy >= goal ? $" ✅ target {Percent(goal)}" : $" ❌ below target {Percent(goal)}") : "";
        markdown.AppendLine(CultureInfo.InvariantCulture, $"**Field accuracy {Percent(Accuracy)}**{verdict} · on-label fields {Percent(OnLabelAccuracy)} · {Cases.Count} labels · {Errors} errors");
        markdown.AppendLine();
        var (correct, incorrect) = MeanConfidence;
        markdown.AppendLine(CultureInfo.InvariantCulture,
            $"Prompt `{PromptVersion}` · model `{(ModelIds.Count == 0 ? "none" : string.Join("`, `", ModelIds))}` · {InputTokens} input / {OutputTokens} output tokens · {Duration.TotalSeconds:0.0} s · mean confidence {correct:0.00} when right, {incorrect:0.00} when wrong");
        markdown.AppendLine();
        markdown.AppendLine("| Field | Accuracy | On label | Match | Partial | Wrong | Missed | Unexpected |");
        markdown.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var field in Fields)
        {
            markdown.AppendLine(CultureInfo.InvariantCulture,
                $"| {field.Field} | {Percent(field.Accuracy)} | {field.OnLabel} | {field.Matched} | {field.Partial} | {field.Wrong} | {field.Missed} | {field.Unexpected} |");
        }

        var misses = Cases.SelectMany(c => c.Fields.Where(f => !f.IsCorrect).Select(f => (c.Id, Field: f))).ToList();
        var errors = Cases.Where(c => c.Error is not null).ToList();
        if (misses.Count > 0 || errors.Count > 0)
        {
            markdown.AppendLine();
            markdown.AppendLine(CultureInfo.InvariantCulture, $"<details><summary>{misses.Count} fields not right</summary>");
            markdown.AppendLine();
            markdown.AppendLine("| Label | Field | Expected | Extracted | Outcome |");
            markdown.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (var error in errors)
            {
                markdown.AppendLine(CultureInfo.InvariantCulture, $"| {error.Id} | (all) | | {Cell(error.Error)} | error |");
            }

            foreach (var (id, field) in misses)
            {
                markdown.AppendLine(CultureInfo.InvariantCulture, $"| {id} | {field.Field} | {Cell(field.Expected)} | {Cell(field.Actual)} | {field.Outcome.ToString().ToLowerInvariant()} |");
            }

            markdown.AppendLine();
            markdown.AppendLine("</details>");
        }

        return markdown.ToString();
    }

    private static string Cell(string? value) => value is null ? "–" : value.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");
}
