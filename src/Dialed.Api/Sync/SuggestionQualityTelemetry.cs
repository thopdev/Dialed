using System.Text.Json.Nodes;
using Dialed.Telemetry;

namespace Dialed.Api.Sync;

/// <summary>
/// Turns a pushed <c>DialInFeedback</c> record into metrics (#89): how often a suggestion of each prompt version is applied or dismissed, and how the
/// rating of the next brew changed after an applied one. Tags are the prompt version, the source and the outcome only; nothing that identifies a person.
/// </summary>
public static class SuggestionQualityTelemetry
{
    private const int MaxVersionLength = 40;

    public static void Record(JsonObject entity, bool isUpdate)
    {
        var version = Version(entity["promptVersion"]?.GetValue<string>());
        var source = entity["source"]?.GetValue<string>() is "ai" ? "ai" : "rules";
        var outcome = Number(entity["outcome"]) switch
        {
            0 => "applied",
            1 => "dismissed",
            _ => null,
        };
        if (outcome is null)
        {
            return;
        }


        // A record is pushed once when it is created and again when the next brew is linked; only the first push counts as an outcome.
        if (!isUpdate)
        {
            DialedTelemetry.AiSuggestionOutcomes.Add(1, new KeyValuePair<string, object?>("prompt_version", version), new KeyValuePair<string, object?>("source", source), new KeyValuePair<string, object?>("outcome", outcome));
        }

        if (outcome == "applied" && isUpdate && Number(entity["ratingBefore"]) is { } before && Number(entity["ratingAfter"]) is { } after
            && before is >= 1 and <= 5 && after is >= 1 and <= 5)
        {
            DialedTelemetry.AiSuggestionRatingChange.Record(after - before, new KeyValuePair<string, object?>("prompt_version", version), new KeyValuePair<string, object?>("source", source));
        }
    }

    /// <summary>Prompt versions are short names like <c>rules-1</c> or <c>dialin-v1</c>; anything else is folded into one tag value so the tag cannot grow without limit.</summary>
    private static string Version(string? value) =>
        value is { Length: > 0 and <= MaxVersionLength } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_') ? value : "other";

    private static int? Number(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
}
