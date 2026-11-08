using Dialed.Domain.BrewMethods;

namespace Dialed.Domain.Model;

/// <summary>What changed between a recipe version and the one it came from.</summary>
public sealed record RecipeDiff(IReadOnlyList<ParameterDelta> Parameters, bool StepsChanged, bool GrinderChanged)
{
    public bool IsEmpty => Parameters.Count == 0 && !StepsChanged && !GrinderChanged;
}

/// <summary>How a version did: the average rating of its rated brews and how many brews it has.</summary>
public sealed record VersionStats(decimal? AverageRating, int BrewCount);

/// <summary>
/// Dial-in history. Changing a recipe you have brewed with does not overwrite it: the change becomes a new version
/// (<see cref="Recipe.ParentRecipeId"/> points at the version it came from) and the old one is marked superseded, so
/// the list shows one recipe while the timeline keeps every version, with its brews and ratings.
/// </summary>
public static class RecipeVersions
{
    public static bool HasBrews(string recipeId, IEnumerable<Brew> brews) =>
        brews.Any(brew => !brew.Deleted && brew.RecipeId == recipeId);

    /// <summary>
    /// Every version connected to <paramref name="recipe"/> through parent links, oldest first. A recipe that was never versioned
    /// is a family of one.
    /// </summary>
    public static IReadOnlyList<Recipe> Family(Recipe recipe, IEnumerable<Recipe> all)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(all);

        var live = all.Where(r => !r.Deleted).ToList();
        var seen = new HashSet<string> { recipe.Id };
        var queue = new Queue<string>([recipe.Id]);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            var node = live.FirstOrDefault(r => r.Id == id) ?? (id == recipe.Id ? recipe : null);
            if (node?.ParentRecipeId is { } parent && seen.Add(parent))
            {
                queue.Enqueue(parent);
            }

            foreach (var child in live.Where(r => r.ParentRecipeId == id))
            {
                if (seen.Add(child.Id))
                {
                    queue.Enqueue(child.Id);
                }
            }
        }

        return [.. live.Where(r => seen.Contains(r.Id)).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id, StringComparer.Ordinal)];
    }

    /// <summary>The version in use (not superseded). When several claim it, the newest wins.</summary>
    public static Recipe? Current(IReadOnlyList<Recipe> family) =>
        family.Where(r => !r.IsSuperseded).OrderByDescending(r => r.CreatedAt).FirstOrDefault() ?? family.LastOrDefault();

    /// <summary>
    /// The records to save for "save as a new version": the edited copy as a new recipe pointing at <paramref name="current"/>,
    /// and <paramref name="current"/> itself marked superseded.
    /// </summary>
    public static (Recipe NewVersion, Recipe Superseded) NewVersion(Recipe current, Recipe edited)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(edited);

        var created = edited with
        {
            Id = Ids.New(),
            Version = 0,
            ParentRecipeId = current.Id,
            IsSuperseded = false,
            IsFavourite = current.IsFavourite,
        };
        return (created, current with { IsSuperseded = true });
    }

    /// <summary>The records to save so that <paramref name="target"/> is the current version of its family and no other is.</summary>
    public static IReadOnlyList<Recipe> MakeCurrent(Recipe target, IReadOnlyList<Recipe> family)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(family);

        var changes = new List<Recipe>();
        foreach (var version in family)
        {
            var shouldBeSuperseded = version.Id != target.Id;
            if (version.IsSuperseded != shouldBeSuperseded)
            {
                changes.Add(version with { IsSuperseded = shouldBeSuperseded });
            }
        }

        return changes;
    }

    public static VersionStats Stats(string recipeId, IEnumerable<Brew> brews)
    {
        var own = brews.Where(brew => !brew.Deleted && brew.RecipeId == recipeId).ToList();
        var rated = own.Where(brew => brew.Rating is not null).Select(brew => (decimal)brew.Rating!.Value).ToList();
        return new VersionStats(rated.Count == 0 ? null : Math.Round(rated.Average(), 1), own.Count);
    }

    /// <summary>The parameters (and steps and grinder) that differ between <paramref name="from"/> and <paramref name="to"/>.</summary>
    public static RecipeDiff Diff(Recipe from, Recipe to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        List<ParameterDelta> rows =
        [
            new(BrewParameter.Dose, from.DoseG, to.DoseG),
            new(BrewParameter.Water, from.WaterG, to.WaterG),
            new(BrewParameter.Yield, from.YieldG, to.YieldG),
            new(BrewParameter.Grind, from.GrindSetting, to.GrindSetting),
            new(BrewParameter.Temperature, from.TempC, to.TempC),
            new(BrewParameter.Time, from.TimeS, to.TimeS),
            new(BrewParameter.Preinfusion, from.PreinfusionS, to.PreinfusionS),
        ];

        return new RecipeDiff(
            [.. rows.Where(row => row.Planned != row.Actual)],
            !from.Steps.SequenceEqual(to.Steps),
            from.GrinderId != to.GrinderId);
    }
}
