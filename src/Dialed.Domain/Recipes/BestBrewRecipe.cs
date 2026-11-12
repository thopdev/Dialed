using Dialed.Domain.BrewMethods;
using Dialed.Domain.Model;

namespace Dialed.Domain.Recipes;

/// <summary>A recipe worked out from the best brews of a bean, and what it was worked out from.</summary>
public sealed record BestBrewRecipeResult(Recipe Draft, int Brews, double AverageRating);

/// <summary>
/// Turns a bean's best-rated brews into a starting recipe: the method with the most brews rated 4 or more, and the middle value of what
/// those brews used (dose, water or yield, temperature, time; the grind setting is the one used most, since settings are steps). Pure.
/// </summary>
public static class BestBrewRecipe
{
    public const int MinRating = 4;
    public const int MinBrews = 2;

    public static BestBrewRecipeResult? Build(Bean bean, IEnumerable<Bag> bags, IEnumerable<Brew> brews, IEnumerable<Recipe> recipes)
    {
        ArgumentNullException.ThrowIfNull(bean);
        ArgumentNullException.ThrowIfNull(bags);
        ArgumentNullException.ThrowIfNull(brews);
        ArgumentNullException.ThrowIfNull(recipes);

        var bagIds = bags.Where(b => !b.Deleted && b.BeanId == bean.Id).Select(b => b.Id).ToHashSet();
        var best = brews
            .Where(b => !b.Deleted && bagIds.Contains(b.BagId) && b.Rating >= MinRating)
            .GroupBy(b => b.MethodCode)
            .Where(g => g.Count() >= MinBrews)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Average(b => b.Rating!.Value))
            .ThenByDescending(g => g.Max(b => b.BrewedAt))
            .FirstOrDefault();
        if (best is null)
        {
            return null;
        }

        var list = best.ToList();
        var brewerByRecipe = recipes.Where(r => !r.Deleted && r.BrewerId is not null).ToDictionary(r => r.Id, r => r.BrewerId!);
        var method = BrewMethodCatalog.Default.Find(best.Key)?.Name ?? best.Key;
        var grinder = Common(list.Select(b => b.GrinderId));

        var draft = new Recipe
        {
            MethodCode = best.Key,
            Name = $"{bean.Name} · {method}",
            BeanId = bean.Id,
            GrinderId = grinder,
            BrewerId = Common(list.Select(b => b.RecipeId is { } id && brewerByRecipe.TryGetValue(id, out var brewer) ? brewer : null)),
            DoseG = Median(list.Select(b => (decimal?)b.DoseG)),
            WaterG = Median(list.Select(b => b.WaterG)),
            YieldG = Median(list.Select(b => b.YieldG)),
            GrindSetting = grinder is null ? null : Common(list.Where(b => b.GrinderId == grinder).Select(b => b.GrindSetting)),
            TempC = Median(list.Select(b => b.TempC)),
            TimeS = Median(list.Select(b => b.TimeS is { } s ? (decimal?)s : null)) is { } time ? (int)Math.Round(time) : null,
        };
        return new BestBrewRecipeResult(draft, list.Count, list.Average(b => b.Rating!.Value));
    }

    /// <summary>The middle of the values that exist, to one decimal; null when none.</summary>
    private static decimal? Median(IEnumerable<decimal?> values)
    {
        var sorted = values.OfType<decimal>().Order().ToList();
        if (sorted.Count == 0)
        {
            return null;
        }

        var middle = sorted.Count / 2;
        var median = sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2m;
        return Math.Round(median, 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>The value used most; ties go to the one that comes first. Null when there is none.</summary>
    private static T? Common<T>(IEnumerable<T?> values)
        where T : struct
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return present.Count == 0 ? null : present.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
    }

    private static string? Common(IEnumerable<string?> values) =>
        values.Where(v => !string.IsNullOrEmpty(v)).GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
}
