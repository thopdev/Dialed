using Dialed.Domain.BrewMethods;
using Dialed.Domain.Equipment;

namespace Dialed.AI.Recipes;

/// <summary>
/// Answers with classic starting points until a Claude endpoint is configured and its answers have been verified against real brews. The shape is
/// the one the model call will return, so the app and the endpoint do not change when it is switched on. With a recipe for a similar bean the
/// numbers start from that one.
/// </summary>
public sealed class RulesRecipeSuggester : IRecipeSuggester
{
    public const string ModelId = "rules";

    /// <summary>The rules have their own version; change it when they change.</summary>
    public const string PromptVersion = "recipe-rules-1";

    public Task<RecipeSuggestionResult> SuggestAsync(RecipeSuggestionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var method = BrewMethodCatalog.Default.Find(context.MethodCode) ?? throw new RecipeSuggestionException("Unknown brew method.");
        var roast = Band(context.RoastLevel);
        var similar = context.Similar;
        List<ParameterReason> reasons = [];

        var (dose, liquid, temp, time, grindFraction) = Baseline(method.Category, roast);
        var fromSimilar = similar is not null;

        if (similar?.DoseG is > 0m)
        {
            dose = similar.DoseG.Value;
        }

        // Keep the ratio of the baseline unless the similar recipe has its own.
        var ratio = liquid / Math.Max(dose, 1m);
        var similarLiquid = method.Category == BrewMethodCategory.Espresso ? similar?.YieldG : similar?.WaterG;
        if (similarLiquid is > 0m && similar?.DoseG is > 0m)
        {
            liquid = similarLiquid.Value;
            ratio = liquid / dose;
        }
        else
        {
            liquid = Math.Round(dose * ratio * 2m, MidpointRounding.AwayFromZero) / 2m;
        }

        if (similar?.TempC is { } similarTemp)
        {
            temp = similarTemp;
        }

        if (similar?.TimeS is { } similarTime)
        {
            time = similarTime;
        }

        decimal? grind = null;
        if (context.GrindMin is { } min && context.GrindMax is { } max && max > min)
        {
            var scale = GrindScale.Number(min, max, context.GrindStep ?? 0m);
            grind = similar?.GrindSetting is { } g ? scale.Snap(g) : scale.Snap(min + ((max - min) * grindFraction));
        }

        var code = fromSimilar ? "SimilarBest" : roast switch { RoastBand.Light => "LightRoast", RoastBand.Dark => "DarkRoast", _ => "Baseline" };
        if (method.Uses(BrewParameter.Dose))
        {
            reasons.Add(new ParameterReason("dose", fromSimilar ? "SimilarBest" : "Baseline"));
        }

        var usesYield = method.Uses(BrewParameter.Yield);
        var usesWater = method.Uses(BrewParameter.Water);
        if (usesYield || usesWater)
        {
            reasons.Add(new ParameterReason(usesYield ? "yield" : "water", fromSimilar ? "SimilarBest" : "Ratio"));
        }

        if (method.Uses(BrewParameter.Grind) && grind is not null)
        {
            reasons.Add(new ParameterReason("grind", fromSimilar && similar?.GrindSetting is not null ? "SimilarBest" : "OnScale"));
        }

        if (method.Uses(BrewParameter.Temperature) && temp is not null)
        {
            reasons.Add(new ParameterReason("temperature", code));
        }

        if (method.Uses(BrewParameter.Time) && time is not null)
        {
            reasons.Add(new ParameterReason("time", context.DaysSinceRoast is <= 4 ? "Fresh" : fromSimilar && similar?.TimeS is not null ? "SimilarBest" : "Baseline"));
        }

        var result = new RecipeSuggestionResult(
            dose,
            usesWater ? liquid : null,
            usesYield ? liquid : null,
            method.Uses(BrewParameter.Grind) ? grind : null,
            method.Uses(BrewParameter.Temperature) ? temp : null,
            method.Uses(BrewParameter.Time) ? time : null,
            reasons,
            PromptVersion,
            ModelId);
        return Task.FromResult(result);
    }

    private enum RoastBand { Light, Medium, Dark }

    private static RoastBand Band(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        "light" or "medium-light" or "medium light" => RoastBand.Light,
        "dark" or "medium-dark" or "medium dark" => RoastBand.Dark,
        _ => RoastBand.Medium,
    };

    /// <summary>Dose, liquid (water or yield), temperature, time and where on the grinder's range the grind sits (0 finest, 1 coarsest).</summary>
    private static (decimal Dose, decimal Liquid, decimal? Temp, int? Time, decimal GrindFraction) Baseline(BrewMethodCategory category, RoastBand roast)
    {
        decimal Heat(decimal medium) => roast switch { RoastBand.Light => medium + 2m, RoastBand.Dark => medium - 3m, _ => medium };
        return category switch
        {
            BrewMethodCategory.Espresso => (18m, roast == RoastBand.Light ? 40m : roast == RoastBand.Dark ? 34m : 36m, Heat(93m) - (roast == RoastBand.Light ? 1m : 0m), 28, 0.2m),
            BrewMethodCategory.PourOver => (15m, 250m, Heat(94m), 180, 0.5m),
            BrewMethodCategory.Immersion => (15m, 250m, Heat(93m), 240, 0.65m),
            BrewMethodCategory.ColdBrew => (60m, 600m, null, 43200, 0.8m),
            _ => (15m, 250m, Heat(93m), 180, 0.5m),
        };
    }
}
