using Dialed.Domain.Model;

namespace Dialed.Domain.Equipment;

/// <summary>The grind setting a grinder did best at for one bean and method: the one of its best-rated brew.</summary>
public sealed record FavoriteGrind(string BeanId, string MethodCode, decimal Setting, int Rating, int Brews);

/// <summary>Which setting works for which bean on one grinder, read from the rated brews. Nothing is stored: it follows the brews.</summary>
public static class FavoriteGrinds
{
    /// <summary>The lowest rating that makes a setting a favourite.</summary>
    public const int MinRating = 4;

    public static IReadOnlyList<FavoriteGrind> For(string grinderId, IEnumerable<Brew> brews, IEnumerable<Bag> bags)
    {
        ArgumentNullException.ThrowIfNull(brews);
        ArgumentNullException.ThrowIfNull(bags);
        var beanOf = bags.Where(b => !b.Deleted).ToDictionary(b => b.Id, b => b.BeanId);
        return
        [
            .. brews
                .Where(b => !b.Deleted && b.GrinderId == grinderId && b.GrindSetting is not null && b.Rating is not null && beanOf.ContainsKey(b.BagId))
                .GroupBy(b => (Bean: beanOf[b.BagId], b.MethodCode))
                .Select(g =>
                {
                    var best = g.OrderByDescending(b => b.Rating).ThenByDescending(b => b.BrewedAt).First();
                    return new FavoriteGrind(g.Key.Bean, g.Key.MethodCode, best.GrindSetting!.Value, best.Rating!.Value, g.Count());
                })
                .Where(f => f.Rating >= MinRating)
                .OrderByDescending(f => f.Rating)
                .ThenBy(f => f.BeanId, StringComparer.Ordinal),
        ];
    }
}
