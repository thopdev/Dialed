using Dialed.Domain.Model;

namespace Dialed.Domain.Barcodes;

/// <summary>
/// Barcodes on bags (#104). Retail codes (EAN-13, EAN-8, UPC-A, UPC-E) are cleaned to digits and accepted only when their check digit
/// is right, so a half-read code never matches or gets saved. Anything else (QR, Code 128) is kept as typed, trimmed.
/// </summary>
public static class BarcodeMatcher
{
    public const int MaxLength = 128;

    /// <summary>
    /// The code in the form it is stored and compared in, or <c>null</c> when it is empty, too long, or looks like a retail code
    /// (8, 12 or 13 digits) whose check digit is wrong. UPC-A is stored as EAN-13 (a leading 0), so the same bag matches either way.
    /// </summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmed = code.Trim();
        if (trimmed.Length > MaxLength)
        {
            return null;
        }

        var digits = new string([.. trimmed.Where(c => c is not ' ' and not '-')]);
        if (digits.Length > 0 && digits.All(char.IsAsciiDigit))
        {
            return digits.Length switch
            {
                13 => HasValidCheckDigit(digits) ? digits : null,
                12 => HasValidCheckDigit("0" + digits) ? "0" + digits : null,
                8 => HasValidCheckDigit(digits) ? digits : null,
                _ => digits.Length >= 4 ? digits : null, // some other numeric code: kept as the digits
            };
        }

        return trimmed;
    }

    /// <summary>The GS1 check digit: the digits before the last, weighted 3 and 1 from the right, add up to a multiple of ten with it.</summary>
    public static bool HasValidCheckDigit(string digits)
    {
        if (digits.Length < 2 || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        var sum = 0;
        for (var i = digits.Length - 2; i >= 0; i--)
        {
            var weight = (digits.Length - 2 - i) % 2 == 0 ? 3 : 1;
            sum += (digits[i] - '0') * weight;
        }

        return (10 - (sum % 10)) % 10 == digits[^1] - '0';
    }

    /// <summary>The same list with each code normalised, invalid ones dropped and duplicates removed, in the order given.</summary>
    public static IReadOnlyList<string> NormalizeAll(IEnumerable<string>? codes) =>
        [.. (codes ?? []).Select(Normalize).OfType<string>().Distinct(StringComparer.Ordinal)];

    /// <summary>The beans that carry <paramref name="code"/>, not deleted, in name order. An invalid code matches nothing.</summary>
    public static IReadOnlyList<Bean> Find(string? code, IEnumerable<Bean> beans)
    {
        ArgumentNullException.ThrowIfNull(beans);

        if (Normalize(code) is not { } normalized)
        {
            return [];
        }

        return
        [
            .. beans
                .Where(b => !b.Deleted && b.Barcodes.Select(Normalize).Contains(normalized, StringComparer.Ordinal))
                .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }
}
