using Dialed.Domain.Model;

namespace Dialed.Domain.Equipment;

/// <summary>How the brews made with one water turned out.</summary>
public sealed record WaterUse(string WaterId, int Brews, int Rated, double? AverageRating);

/// <summary>Compares brewing waters (gear of kind Water) by the rating of the brews made with them. Pure.</summary>
public static class WaterStats
{
    /// <summary>One entry per water that was used at least once, best average rating first (unrated last).</summary>
    public static IReadOnlyList<WaterUse> Build(IEnumerable<Brew> brews)
    {
        ArgumentNullException.ThrowIfNull(brews);
        return
        [
            .. brews
                .Where(b => !b.Deleted && b.WaterId is not null)
                .GroupBy(b => b.WaterId!)
                .Select(g =>
                {
                    var rated = g.Where(b => b.Rating is not null).ToList();
                    return new WaterUse(g.Key, g.Count(), rated.Count, rated.Count == 0 ? null : rated.Average(b => (double)b.Rating!.Value));
                })
                .OrderByDescending(w => w.AverageRating ?? -1)
                .ThenByDescending(w => w.Brews),
        ];
    }
}
