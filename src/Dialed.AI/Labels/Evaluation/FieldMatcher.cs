using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Dialed.AI.Labels.Evaluation;

/// <summary>How one extracted field compares with the expected value.</summary>
public enum FieldOutcome
{
    /// <summary>Both say the label does not have this field.</summary>
    BothEmpty,

    /// <summary>Same value after normalisation.</summary>
    Match,

    /// <summary>A list field where some items match (score between 0 and 1).</summary>
    Partial,

    /// <summary>Both have a value, but they differ.</summary>
    Wrong,

    /// <summary>The label has the field, the extraction left it out.</summary>
    Missed,

    /// <summary>The extraction has a value the label does not have (a guess or hallucination).</summary>
    Unexpected,
}

/// <summary>The comparison of one field. <see cref="Score"/> is 1 for a match, 0 for a miss, and the F1 of the items for a partial list.</summary>
public sealed record FieldScore(string Field, string? Expected, string? Actual, double Confidence, FieldOutcome Outcome, double Score)
{
    public bool IsCorrect => Outcome is FieldOutcome.Match or FieldOutcome.BothEmpty;

    /// <summary>True when the label has this field (the expected value is not empty).</summary>
    public bool IsOnLabel => !string.IsNullOrWhiteSpace(Expected);
}

/// <summary>
/// Compares extracted label fields with expected ones, field by field, after normalisation. Text is compared case and accent
/// insensitive with a small edit-distance allowance; process and roast level use their canonical names; lists are compared as
/// sets; dates and weights are parsed. See docs/ai-label-golden-set.md for the rules.
/// </summary>
public sealed partial class FieldMatcher
{
    /// <summary>Similarity (1 - edit distance / length) from which two texts count as the same. 0.85 allows one typo in seven letters.</summary>
    public double TextThreshold { get; init; } = 0.85;

    /// <summary>Relative difference allowed between two weights, so 12 oz (340.2 g) matches 340 g.</summary>
    public double WeightTolerance { get; init; } = 0.02;

    private enum Kind { Text, Process, RoastLevel, List, Date, Weight }

    private static readonly Dictionary<string, Kind> Kinds = new(StringComparer.Ordinal)
    {
        ["name"] = Kind.Text,
        ["roaster"] = Kind.Text,
        ["country"] = Kind.Text,
        ["region"] = Kind.Text,
        ["farm"] = Kind.Text,
        ["process"] = Kind.Process,
        ["varieties"] = Kind.List,
        ["roastLevel"] = Kind.RoastLevel,
        ["tastingNotes"] = Kind.List,
        ["roastDate"] = Kind.Date,
        ["weightG"] = Kind.Weight,
    };

    /// <summary>Spellings that mean the same thing, after normalisation. Keep it small: the golden set holds what the label prints.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        // Countries
        ["usa"] = "united states",
        ["us"] = "united states",
        ["drc"] = "dr congo",
        ["democratic republic of the congo"] = "dr congo",
        ["democratic republic of congo"] = "dr congo",
        ["png"] = "papua new guinea",

        // Processes
        ["fully washed"] = "washed",
        ["wet"] = "washed",
        ["wet processed"] = "washed",
        ["lavado"] = "washed",
        ["gewassen"] = "washed",
        ["dry"] = "natural",
        ["dry processed"] = "natural",
        ["sun dried"] = "natural",
        ["natuurlijk"] = "natural",
        ["pulped natural"] = "honey",
        ["semi washed"] = "wet hulled",
        ["giling basah"] = "wet hulled",
    };

    private static readonly string[] RoastLevels = ["light", "medium-light", "medium", "medium-dark", "dark"];

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy-M-d", "yyyy/MM/dd", "dd-MM-yyyy", "d-M-yyyy", "dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy", "d.M.yyyy",
        "d MMMM yyyy", "d MMM yyyy", "MMMM d yyyy", "MMM d yyyy", "MMMM d, yyyy", "MMM d, yyyy",
    ];

    private static readonly CultureInfo[] DateCultures = [CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("nl-NL")];

    public static IReadOnlyCollection<string> FieldNames => Kinds.Keys;

    public FieldScore Score(string field, string? expected, DraftField actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        if (!Kinds.TryGetValue(field, out var kind))
        {
            throw new ArgumentException($"Unknown label field '{field}'.", nameof(field));
        }

        var expectedValue = string.IsNullOrWhiteSpace(expected) ? null : expected.Trim();
        var actualValue = actual.HasValue ? actual.Value!.Trim() : null;
        var confidence = actualValue is null ? 0 : actual.Confidence;

        FieldScore Result(FieldOutcome outcome, double score) => new(field, expectedValue, actualValue, confidence, outcome, score);

        if (expectedValue is null)
        {
            return actualValue is null ? Result(FieldOutcome.BothEmpty, 1) : Result(FieldOutcome.Unexpected, 0);
        }

        if (actualValue is null)
        {
            return Result(FieldOutcome.Missed, 0);
        }

        if (kind == Kind.List)
        {
            var f1 = ListScore(expectedValue, actualValue);
            return f1 >= 1 ? Result(FieldOutcome.Match, 1) : f1 > 0 ? Result(FieldOutcome.Partial, f1) : Result(FieldOutcome.Wrong, 0);
        }

        var same = kind switch
        {
            Kind.Text => TextEquals(expectedValue, actualValue),
            Kind.Process => TextEquals(Process(expectedValue), Process(actualValue)),
            Kind.RoastLevel => RoastLevel(expectedValue) is { } level && level == RoastLevel(actualValue),
            Kind.Date => DateEquals(expectedValue, actualValue),
            Kind.Weight => WeightEquals(expectedValue, actualValue),
            _ => false,
        };
        return same ? Result(FieldOutcome.Match, 1) : Result(FieldOutcome.Wrong, 0);
    }

    /// <summary>Lower case, accents removed, "&amp;" read as "and", punctuation to spaces, single spaces, aliases applied.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var decomposed = text.ToLowerInvariant().Replace("&", " and ", StringComparison.Ordinal).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        var normalized = Spaces().Replace(builder.ToString(), " ").Trim();
        return Aliases.TryGetValue(normalized, out var alias) ? alias : normalized;
    }

    /// <summary>1 minus the edit distance divided by the longer length, on normalised text.</summary>
    public static double Similarity(string a, string b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        if (x.Length == 0 && y.Length == 0)
        {
            return 1;
        }

        return 1 - (double)Levenshtein(x, y) / Math.Max(x.Length, y.Length);
    }

    /// <summary>Splits a list field on commas, semicolons, slashes, ampersands, bullets, spaced dashes, new lines and the words "and"/"en".</summary>
    public static IReadOnlyList<string> SplitList(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ListSeparators().Split(text)
            .Select(Normalize)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private bool TextEquals(string expected, string actual) => Similarity(expected, actual) >= TextThreshold;

    private static string Process(string text)
    {
        var normalized = ProcessNoise().Replace(Normalize(text), " ");
        normalized = Spaces().Replace(normalized, " ").Trim();
        return Aliases.TryGetValue(normalized, out var alias) ? alias : normalized;
    }

    private static string? RoastLevel(string text)
    {
        var normalized = Normalize(text).Replace(" roast", "", StringComparison.Ordinal).Replace("roast ", "", StringComparison.Ordinal);
        var dashed = normalized.Trim().Replace(' ', '-');
        return RoastLevels.FirstOrDefault(level => level == dashed);
    }

    /// <summary>F1 of the items: each expected item is matched at most once with a fuzzy text match.</summary>
    private double ListScore(string expected, string actual)
    {
        var want = SplitList(expected);
        var got = SplitList(actual).ToList();
        if (want.Count == 0 || got.Count == 0)
        {
            return 0;
        }

        var matched = 0;
        foreach (var item in want)
        {
            var index = got.FindIndex(candidate => Similarity(item, candidate) >= TextThreshold);
            if (index >= 0)
            {
                matched++;
                got.RemoveAt(index);
            }
        }

        if (matched == 0)
        {
            return 0;
        }

        var precision = (double)matched / SplitList(actual).Count;
        var recall = (double)matched / want.Count;
        return 2 * precision * recall / (precision + recall);
    }

    private static bool DateEquals(string expected, string actual)
    {
        if (!TryParseDate(expected, out var want))
        {
            return false;
        }

        // Any reading of the extracted date that gives the expected day counts: the prompt asks for yyyy-MM-dd, so this only forgives the format.
        var cleaned = Spaces().Replace(DateNoise().Replace(actual.Trim(), " "), " ").Trim();
        return DateCultures.Any(culture => DateFormats.Any(format =>
            DateOnly.TryParseExact(cleaned, format, culture, DateTimeStyles.AllowWhiteSpaces, out var day) && day == want));
    }

    public static bool TryParseDate(string text, out DateOnly date) =>
        DateOnly.TryParseExact(text?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private bool WeightEquals(string expected, string actual)
    {
        if (!TryParseGrams(expected, out var want) || !TryParseGrams(actual, out var got) || want <= 0)
        {
            return false;
        }

        return Math.Abs(want - got) / want <= WeightTolerance;
    }

    /// <summary>Reads a weight such as "250", "250 g", "1 kg", "12 oz" or "0,5 kg" as grams.</summary>
    public static bool TryParseGrams(string? text, out double grams)
    {
        grams = 0;
        var match = text is null ? Match.Empty : WeightPattern().Match(text);
        if (!match.Success || !double.TryParse(match.Groups["number"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        var factor = match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "" or "g" or "gr" or "gram" or "grams" or "gramm" => 1,
            "kg" or "kilo" or "kilogram" => 1000,
            "oz" or "ounce" or "ounces" => 28.3495,
            "lb" or "lbs" or "pound" => 453.592,
            _ => 0,
        };
        grams = number * factor;
        return factor > 0;
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s*(?:[,;/&|•·\r\n]+|\s[-–—]\s|\band\b|\ben\b)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ListSeparators();

    [GeneratedRegex(@"\b(process|processed|processing|method|proces)\b")]
    private static partial Regex ProcessNoise();

    [GeneratedRegex(@"(?i)\b(roasted|roast|date|on|geroosterd|op)\b|:")]
    private static partial Regex DateNoise();

    [GeneratedRegex(@"(?<number>\d+(?:[.,]\d+)?)(?:\s*(?<unit>kilogram|kilo|kg|grams|gramm|gram|gr|g|ounces|ounce|oz|lbs|lb|pound)\b|(?!\s*\p{L}))", RegexOptions.IgnoreCase)]
    private static partial Regex WeightPattern();
}
