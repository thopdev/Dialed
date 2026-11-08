using System.Globalization;
using System.Text;

namespace Dialed.Domain.Text;

/// <summary>
/// Forgiving name matching for pickers ("friedhat" finds "Friedhats Coffee Roasters", "Cafe" finds "Café"):
/// ignores case, accents and punctuation, and tolerates small typos.
/// </summary>
public static class FuzzyMatch
{
    /// <summary>Lower case, accents removed, anything but letters and digits turned into single spaces.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// How well <paramref name="candidate"/> matches what was typed: 0 (no match) to 1 (same). Exact and prefix matches rank
    /// above words that merely contain the text, which rank above typos.
    /// </summary>
    public static double Score(string? query, string? candidate)
    {
        var q = Normalize(query);
        var c = Normalize(candidate);
        if (q.Length == 0 || c.Length == 0)
        {
            return 0d;
        }

        if (q == c)
        {
            return 1d;
        }

        if (c.StartsWith(q, StringComparison.Ordinal))
        {
            return 0.9d;
        }

        var words = c.Split(' ');
        if (words.Any(word => word.StartsWith(q, StringComparison.Ordinal)))
        {
            return 0.8d;
        }

        if (c.Contains(q, StringComparison.Ordinal))
        {
            return 0.7d;
        }

        // Typos: compare with the start of the candidate and with each word, allowing about one mistake per four letters.
        var allowed = q.Length / 4;
        if (allowed == 0)
        {
            return 0d;
        }

        var best = int.MaxValue;
        foreach (var word in words.Append(c.Length > q.Length ? c[..q.Length] : c))
        {
            var target = word.Length > q.Length ? word[..q.Length] : word;
            best = Math.Min(best, Distance(q, target));
        }

        return best <= allowed ? 0.5d - (0.1d * best / Math.Max(1, allowed)) : 0d;
    }

    /// <summary>Items that match, best first (ties keep their order). Empty queries match nothing.</summary>
    public static IReadOnlyList<T> Rank<T>(IEnumerable<T> items, string? query, Func<T, string?> name)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(name);

        return [.. items
            .Select((item, index) => (item, index, score: Score(query, name(item))))
            .Where(entry => entry.score > 0d)
            .OrderByDescending(entry => entry.score)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.item)];
    }

    private static int Distance(string a, string b)
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
}
