using Dialed.Domain.BrewMethods;
using Dialed.Domain.Equipment;
using Dialed.Domain.Model;

namespace Dialed.Domain.DialIn;

/// <summary>
/// Rule-based advice after a brew, with no network (#85): from how it tasted and what was logged, one thing to change first and one
/// to try otherwise. Rules favour the classic fixes: sour means under-extracted (grind finer, hotter), bitter means over-extracted
/// (grind coarser, cooler), weak or strong is about dose and ratio. Grind direction assumes that a higher setting is coarser.
/// </summary>
public static class DialInAdvisor
{
    /// <summary>Recorded with every accepted or dismissed suggestion; change it when the rules change.</summary>
    public const string RulesVersion = "rules-1";

    /// <summary>A usual espresso shot time; faster is likely under-extracted, slower over-extracted.</summary>
    public const int EspressoFastBelowS = 25;

    public const int EspressoSlowAboveS = 35;

    private const decimal MinTempC = 80m;
    private const decimal MaxTempC = 100m;

    /// <param name="method">The brew method of the brew.</param>
    /// <param name="brew">The brew with its taste and the values actually used.</param>
    /// <param name="grindScale">The grinder's scale, so the grind suggestion is a valid setting; <c>null</c> when unknown.</param>
    public static DialInAdvice Advise(BrewMethod method, Brew brew, GrindScale? grindScale = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(brew);

        var espresso = method.Category == BrewMethodCategory.Espresso;
        var taste = brew.Taste;
        List<DialInSuggestion?> candidates = [];

        // Acidity first: it says most about extraction.
        if (taste.Acidity < 0)
        {
            candidates.Add(Grind(brew, grindScale, finer: true, SuggestionReason.Sour));
            candidates.Add(Temperature(brew, espresso ? 1m : 2m, SuggestionReason.Sour));
        }
        else if (taste.Acidity > 0)
        {
            candidates.Add(Grind(brew, grindScale, finer: false, SuggestionReason.Bitter));
            candidates.Add(Temperature(brew, espresso ? -1m : -2m, SuggestionReason.Bitter));
        }

        // Strength: dose and ratio.
        if (taste.Strength < 0)
        {
            candidates.Add(Dose(brew, espresso, increase: true, SuggestionReason.Weak));
            candidates.Add(Ratio(brew, espresso, longer: false, SuggestionReason.Weak));
        }
        else if (taste.Strength > 0)
        {
            candidates.Add(Ratio(brew, espresso, longer: true, SuggestionReason.Strong));
            candidates.Add(Dose(brew, espresso, increase: false, SuggestionReason.Strong));
        }

        // Body: thin wants more coffee in the cup, heavy wants less.
        if (taste.Body < 0)
        {
            candidates.Add(Dose(brew, espresso, increase: true, SuggestionReason.Thin));
            candidates.Add(Ratio(brew, espresso, longer: false, SuggestionReason.Thin));
        }
        else if (taste.Body > 0)
        {
            candidates.Add(Ratio(brew, espresso, longer: true, SuggestionReason.Heavy));
        }

        // Flat (no sweetness) often means too little extraction.
        if (taste.Sweetness < 0)
        {
            candidates.Add(Temperature(brew, espresso ? 1m : 2m, SuggestionReason.Flat));
            candidates.Add(Grind(brew, grindScale, finer: true, SuggestionReason.Flat));
        }

        // A shot that ran outside the usual window points the same way even when the cup tasted fine.
        if (espresso && brew.TimeS is { } time)
        {
            if (time < EspressoFastBelowS)
            {
                candidates.Add(Grind(brew, grindScale, finer: true, SuggestionReason.ShotTooFast));
            }
            else if (time > EspressoSlowAboveS)
            {
                candidates.Add(Grind(brew, grindScale, finer: false, SuggestionReason.ShotTooSlow));
            }
        }

        var distinct = candidates
            .OfType<DialInSuggestion>()
            .DistinctBy(s => s.Kind)
            .Take(2)
            .ToList();

        return distinct.Count == 0
            ? new DialInAdvice(new DialInSuggestion(SuggestionKind.Keep, SuggestionReason.Balanced), null)
            : new DialInAdvice(distinct[0], distinct.Count > 1 ? distinct[1] : null);
    }

    private static DialInSuggestion? Grind(Brew brew, GrindScale? scale, bool finer, SuggestionReason reason)
    {
        var kind = finer ? SuggestionKind.GrindFiner : SuggestionKind.GrindCoarser;
        if (scale is null || brew.GrindSetting is not { } current)
        {
            return new DialInSuggestion(kind, reason, brew.GrindSetting);
        }

        // A stepped grinder moves one step; a stepless one moves 2% of its range.
        var step = scale.IsStepped ? scale.Step : (scale.Max - scale.Min) / 50m;
        var target = scale.Snap(current + (finer ? -step : step));
        return target == scale.Snap(current) ? null : new DialInSuggestion(kind, reason, current, target);
    }

    private static DialInSuggestion? Dose(Brew brew, bool espresso, bool increase, SuggestionReason reason)
    {
        var step = espresso ? 0.5m : 1m;
        var target = brew.DoseG + (increase ? step : -step);
        return target <= 0m
            ? null
            : new DialInSuggestion(increase ? SuggestionKind.IncreaseDose : SuggestionKind.DecreaseDose, reason, brew.DoseG, target);
    }

    private static DialInSuggestion? Ratio(Brew brew, bool espresso, bool longer, SuggestionReason reason)
    {
        var kind = longer ? SuggestionKind.LongerRatio : SuggestionKind.ShorterRatio;
        var liquid = espresso ? brew.YieldG : brew.WaterG;
        if (liquid is not { } current || current <= 0m)
        {
            return new DialInSuggestion(kind, reason);
        }

        // Espresso moves the yield by 2 g; filter coffee moves the water by about 5%, in whole 5 g steps.
        var step = espresso ? 2m : Math.Max(5m, Math.Round(current * 0.05m / 5m, MidpointRounding.AwayFromZero) * 5m);
        var target = current + (longer ? step : -step);
        return target <= 0m ? null : new DialInSuggestion(kind, reason, current, target);
    }

    private static DialInSuggestion? Temperature(Brew brew, decimal delta, SuggestionReason reason)
    {
        var kind = delta > 0m ? SuggestionKind.HotterWater : SuggestionKind.CoolerWater;
        if (brew.TempC is not { } current)
        {
            return new DialInSuggestion(kind, reason);
        }

        var target = Math.Clamp(current + delta, MinTempC, MaxTempC);
        return target == current ? null : new DialInSuggestion(kind, reason, current, target);
    }
}
