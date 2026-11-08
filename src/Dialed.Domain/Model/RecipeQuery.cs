namespace Dialed.Domain.Model;

/// <summary>What the recipe list is narrowed to. A <c>null</c> member means "any".</summary>
public sealed record RecipeFilter(string? BeanId = null, bool FavouritesOnly = false, decimal? MinRating = null);

/// <summary>A method and its recipes, as shown in the grouped list.</summary>
public sealed record RecipeGroup(string MethodCode, IReadOnlyList<Recipe> Recipes);

public static class RecipeQuery
{
    /// <summary>
    /// Filters the recipes and groups them by method (groups ordered by <paramref name="methodName"/>, recipes by favourite, then name).
    /// <paramref name="averageRatings"/> maps a recipe id to the average rating of its brews (from the brew log); a recipe without brews has none.
    /// </summary>
    public static IReadOnlyList<RecipeGroup> Group(
        IEnumerable<Recipe> recipes,
        RecipeFilter filter,
        Func<string, string> methodName,
        IReadOnlyDictionary<string, decimal>? averageRatings = null)
    {
        ArgumentNullException.ThrowIfNull(recipes);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(methodName);

        var query = recipes.Where(r => !r.Deleted);
        if (filter.BeanId is not null)
        {
            query = query.Where(r => r.BeanId == filter.BeanId);
        }

        if (filter.FavouritesOnly)
        {
            query = query.Where(r => r.IsFavourite);
        }

        if (filter.MinRating is { } min)
        {
            query = query.Where(r => averageRatings is not null && averageRatings.TryGetValue(r.Id, out var rating) && rating >= min);
        }

        return [.. query
            .GroupBy(r => r.MethodCode, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => methodName(g.Key), StringComparer.OrdinalIgnoreCase)
            .Select(g => new RecipeGroup(g.Key, [.. g.OrderByDescending(r => r.IsFavourite).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)]))];
    }

    /// <summary>
    /// The recipe to reach for with this bean: best average rating when brews have been rated, then favourites,
    /// then the one changed most recently. <c>null</c> when no recipe is linked to the bean.
    /// </summary>
    public static Recipe? BestForBean(string beanId, IEnumerable<Recipe> recipes, IReadOnlyDictionary<string, decimal>? averageRatings = null)
    {
        ArgumentNullException.ThrowIfNull(recipes);
        return recipes
            .Where(r => !r.Deleted && r.BeanId == beanId)
            .OrderByDescending(r => averageRatings is not null && averageRatings.TryGetValue(r.Id, out var rating) ? rating : -1m)
            .ThenByDescending(r => r.IsFavourite)
            .ThenByDescending(r => r.UpdatedAt)
            .FirstOrDefault();
    }
}
