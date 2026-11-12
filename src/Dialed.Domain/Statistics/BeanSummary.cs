using Dialed.Domain.Model;

namespace Dialed.Domain.Statistics;

/// <summary>What a bean's brews add up to: how often, how well, which method and which way it tends to taste.</summary>
public sealed record BeanSummary(string BeanId, int Bags, int Brews, int Rated, double? AverageRating, int FourStarOrBetter, string? FavouriteMethod, DateTimeOffset? LastBrewed, BeanTasteResult? Taste);

/// <summary>Sums up one bean for comparing two beans side by side. Pure.</summary>
public static class BeanSummaries
{
    public static BeanSummary Build(string beanId, IEnumerable<Bag> bags, IEnumerable<Brew> brews)
    {
        ArgumentNullException.ThrowIfNull(beanId);
        ArgumentNullException.ThrowIfNull(bags);
        ArgumentNullException.ThrowIfNull(brews);

        var beanBags = bags.Where(b => !b.Deleted && b.BeanId == beanId).Select(b => b.Id).ToHashSet();
        var mine = brews.Where(b => !b.Deleted && beanBags.Contains(b.BagId)).ToList();
        var rated = mine.Where(b => b.Rating is not null).ToList();
        return new BeanSummary(
            beanId,
            beanBags.Count,
            mine.Count,
            rated.Count,
            rated.Count == 0 ? null : rated.Average(b => (double)b.Rating!.Value),
            rated.Count(b => b.Rating >= 4),
            mine.GroupBy(b => b.MethodCode).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).FirstOrDefault(),
            mine.Count == 0 ? null : mine.Max(b => b.BrewedAt),
            BeanTasteProfile.Build(beanBags, mine));
    }
}
