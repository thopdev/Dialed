namespace Dialed.Domain.Model;

/// <summary>Finds the person's own recipe to start a suggestion from (#87): the best one for this bean, else for the most similar bean.</summary>
public static class SimilarBeanRecipes
{
    /// <summary>
    /// The best recipe for <paramref name="methodCode"/> among recipes of this bean, then of beans from the same country and process, the same
    /// country, or the same process. Within a group the best average rating wins, then favourites, then the newest. <c>null</c> when nothing fits.
    /// </summary>
    public static Recipe? Best(Bean bean, string methodCode, IEnumerable<Bean> beans, IEnumerable<Recipe> recipes, IReadOnlyDictionary<string, decimal>? averageRatings = null)
    {
        ArgumentNullException.ThrowIfNull(bean);
        ArgumentNullException.ThrowIfNull(beans);
        ArgumentNullException.ThrowIfNull(recipes);

        var byId = beans.Where(b => !b.Deleted).ToDictionary(b => b.Id);
        int Closeness(Recipe recipe)
        {
            if (recipe.BeanId == bean.Id)
            {
                return 4;
            }

            if (recipe.BeanId is null || !byId.TryGetValue(recipe.BeanId, out var other))
            {
                return 0;
            }

            var country = Same(bean.Origin.Country, other.Origin.Country);
            var process = Same(bean.Process, other.Process);
            return country && process ? 3 : country ? 2 : process ? 1 : 0;
        }

        return recipes
            .Where(r => !r.Deleted && !r.IsSuperseded && !r.IsDefaultTemplate && string.Equals(r.MethodCode, methodCode, StringComparison.OrdinalIgnoreCase))
            .Select(r => (Recipe: r, Closeness: Closeness(r)))
            .Where(x => x.Closeness > 0)
            .OrderByDescending(x => x.Closeness)
            .ThenByDescending(x => averageRatings is not null && averageRatings.TryGetValue(x.Recipe.Id, out var rating) ? rating : -1m)
            .ThenByDescending(x => x.Recipe.IsFavourite)
            .ThenByDescending(x => x.Recipe.UpdatedAt)
            .Select(x => x.Recipe)
            .FirstOrDefault();
    }

    private static bool Same(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
