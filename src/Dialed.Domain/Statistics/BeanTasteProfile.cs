using Dialed.Domain.Model;

namespace Dialed.Domain.Statistics;

public enum TasteAxis
{
    Acidity,
    Strength,
    Body,
    Sweetness,
}

/// <summary>How far a bean's brews lean on one axis: <see cref="Average"/> runs from -2 to +2, 0 balanced (see <see cref="Taste"/>).</summary>
public sealed record TasteLean(TasteAxis Axis, double Average)
{
    /// <summary>Whether it leans enough to mention (half a step either way).</summary>
    public bool IsNoticeable => Math.Abs(Average) >= BeanTasteProfile.Noticeable;
}

/// <summary>How a bean tends to come out, from the brews where something was off, and how many brews that is.</summary>
public sealed record BeanTasteResult(int Brews, IReadOnlyList<TasteLean> Axes);

/// <summary>
/// A bean's tendencies: the average of each taste axis over its brews that were not balanced. A brew with no feedback looks the same as a balanced
/// one, so only brews where the person marked something off are counted; the profile answers "when it is off, which way?". Needs at least two.
/// </summary>
public static class BeanTasteProfile
{
    public const int MinBrews = 2;
    public const double Noticeable = 0.5;

    public static BeanTasteResult? Build(IEnumerable<string> bagIds, IEnumerable<Brew> brews)
    {
        ArgumentNullException.ThrowIfNull(bagIds);
        ArgumentNullException.ThrowIfNull(brews);

        var bags = bagIds.ToHashSet();
        var off = brews.Where(b => !b.Deleted && bags.Contains(b.BagId) && !b.Taste.IsBalanced).ToList();
        if (off.Count < MinBrews)
        {
            return null;
        }

        return new BeanTasteResult(
            off.Count,
            [
                new(TasteAxis.Acidity, off.Average(b => (double)b.Taste.Acidity)),
                new(TasteAxis.Strength, off.Average(b => (double)b.Taste.Strength)),
                new(TasteAxis.Body, off.Average(b => (double)b.Taste.Body)),
                new(TasteAxis.Sweetness, off.Average(b => (double)b.Taste.Sweetness)),
            ]);
    }
}
