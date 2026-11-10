using Dialed.Domain.BrewMethods;
using Dialed.Domain.Model;

namespace Dialed.Domain.DialIn;

/// <summary>Puts a suggestion's new value into the values of the next brew (#86).</summary>
public static class DialInApply
{
    /// <summary>
    /// <paramref name="brew"/> with the suggested value in place of the current one. A suggestion without a target value (no grinder scale,
    /// nothing logged) or <see cref="SuggestionKind.Keep"/> changes nothing.
    /// </summary>
    public static Brew Apply(Brew brew, BrewMethod method, DialInSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(brew);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(suggestion);

        if (suggestion.To is not { } to)
        {
            return brew;
        }

        var espresso = method.Category == BrewMethodCategory.Espresso;
        return suggestion.Kind switch
        {
            SuggestionKind.GrindFiner or SuggestionKind.GrindCoarser => brew with { GrindSetting = to },
            SuggestionKind.IncreaseDose or SuggestionKind.DecreaseDose => brew with { DoseG = to },
            SuggestionKind.LongerRatio or SuggestionKind.ShorterRatio => espresso ? brew with { YieldG = to } : brew with { WaterG = to },
            SuggestionKind.HotterWater or SuggestionKind.CoolerWater => brew with { TempC = to },
            _ => brew,
        };
    }

    /// <summary>Whether applying the suggestion would change anything.</summary>
    public static bool CanApply(DialInSuggestion suggestion) => suggestion.Kind != SuggestionKind.Keep && suggestion.To is not null;
}
