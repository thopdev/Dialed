using System.Diagnostics;

namespace Dialed.AI.Labels.Evaluation;

/// <summary>The result of one golden case.</summary>
public sealed record CaseResult(string Id, IReadOnlyList<FieldScore> Fields, string? Error, string? ModelId, int InputTokens, int OutputTokens, TimeSpan Duration)
{
    public double Accuracy => Fields.Count == 0 ? 0 : Fields.Average(f => f.Score);
}

/// <summary>Runs a golden set through any <see cref="ILabelExtractor"/> (fake, Claude in Foundry, or a test double) and scores every field.</summary>
public sealed class LabelEvaluator(ILabelExtractor extractor, FieldMatcher? matcher = null, TimeProvider? time = null)
{
    private readonly FieldMatcher _matcher = matcher ?? new FieldMatcher();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>One case at a time, so a real model is not hit by bursts (the API rate limit and quotas apply to the evaluation too).</summary>
    public async Task<EvaluationReport> RunAsync(GoldenSet set, IProgress<CaseResult>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(set);
        var started = _time.GetTimestamp();
        var results = new List<CaseResult>(set.Cases.Count);
        string? promptVersion = null;
        foreach (var item in set.Cases)
        {
            var result = await RunCaseAsync(item, cancellationToken);
            promptVersion ??= result.Version;
            results.Add(result.Result);
            progress?.Report(result.Result);
        }

        return new EvaluationReport(
            promptVersion ?? LabelPrompt.Version,
            results,
            _time.GetElapsedTime(started),
            _time.GetUtcNow());
    }

    private async Task<(CaseResult Result, string? Version)> RunCaseAsync(GoldenCase item, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var image = await item.ReadImageAsync(cancellationToken);
        LabelExtraction? extraction = null;
        string? error = null;
        try
        {
            extraction = await extractor.ExtractAsync(image, cancellationToken);
        }
        catch (LabelExtractionException exception)
        {
            // Counts as an empty draft: every field on the label is missed, which is what the person would see (manual entry).
            error = exception.Message;
        }

        var draft = extraction?.Draft ?? BeanDraft.Empty;
        var fields = BeanDraft.FieldNames.Select(name => _matcher.Score(name, item.ExpectedValue(name), Get(draft, name))).ToList();
        var result = new CaseResult(item.Id, fields, error, extraction?.ModelId, extraction?.InputTokens ?? 0, extraction?.OutputTokens ?? 0, Stopwatch.GetElapsedTime(started));
        return (result, extraction?.PromptVersion);
    }

    public static DraftField Get(BeanDraft draft, string field)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return field switch
        {
            "name" => draft.Name,
            "roaster" => draft.Roaster,
            "country" => draft.Country,
            "region" => draft.Region,
            "farm" => draft.Farm,
            "process" => draft.Process,
            "varieties" => draft.Varieties,
            "roastLevel" => draft.RoastLevel,
            "tastingNotes" => draft.TastingNotes,
            "roastDate" => draft.RoastDate,
            "weightG" => draft.WeightG,
            _ => throw new ArgumentException($"Unknown label field '{field}'.", nameof(field)),
        };
    }
}
