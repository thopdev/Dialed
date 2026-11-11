using Dialed.Domain.BrewMethods;
using Dialed.Domain.Model;

namespace Dialed.Domain.DialIn;

/// <summary>What the earlier brews of a bag say about a suggestion (#86): when and whether to show advice, and whether a change was already tried.</summary>
public static class DialInHistory
{
    /// <summary>The highest rating that still asks for advice after logging.</summary>
    public const int AdviseAtOrBelowRating = 3;

    /// <summary>Whether a brew is worth advice right after logging it: a rating of 3 or lower, or a taste that is off in any direction.</summary>
    public static bool NeedsAdvice(Brew brew)
    {
        ArgumentNullException.ThrowIfNull(brew);
        return brew.Rating is <= AdviseAtOrBelowRating || !brew.Taste.IsBalanced;
    }

    /// <summary>
    /// Whether going from <paramref name="earlier"/> to <paramref name="current"/> already made the change <paramref name="kind"/> asks for,
    /// without a better rating. Then suggesting the same change again is unlikely to help. Unrated brews never count as tried.
    /// </summary>
    public static bool AlreadyTried(SuggestionKind kind, BrewMethod method, Brew current, Brew earlier)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(earlier);

        if (current.Rating is not { } now || earlier.Rating is not { } before || now > before)
        {
            return false;
        }

        var espresso = method.Category == BrewMethodCategory.Espresso;
        return kind switch
        {
            SuggestionKind.GrindFiner => Moved(earlier.GrindSetting, current.GrindSetting) < 0,
            SuggestionKind.GrindCoarser => Moved(earlier.GrindSetting, current.GrindSetting) > 0,
            SuggestionKind.IncreaseDose => Moved(earlier.DoseG, current.DoseG) > 0,
            SuggestionKind.DecreaseDose => Moved(earlier.DoseG, current.DoseG) < 0,
            SuggestionKind.HotterWater => Moved(earlier.TempC, current.TempC) > 0,
            SuggestionKind.CoolerWater => Moved(earlier.TempC, current.TempC) < 0,
            SuggestionKind.LongerRatio => Moved(Ratio(earlier, espresso), Ratio(current, espresso)) > 0,
            SuggestionKind.ShorterRatio => Moved(Ratio(earlier, espresso), Ratio(current, espresso)) < 0,
            _ => false,
        };
    }

    private static int Moved(decimal? from, decimal? to) => from is { } a && to is { } b ? b.CompareTo(a) : 0;

    private static decimal? Ratio(Brew brew, bool espresso) =>
        (espresso ? brew.YieldG : brew.WaterG) is { } liquid && brew.DoseG > 0m ? liquid / brew.DoseG : null;
}
