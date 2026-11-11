using Dialed.Domain.Model;

namespace Dialed.Domain.Statistics;

/// <summary>One brew on a dial-in trace: when, which grind setting, how long and how it was rated.</summary>
public sealed record DialInPoint(string BrewId, DateTimeOffset At, decimal Grind, int? TimeS, int? Rating);

/// <summary>The brews of one method, oldest first, and the point worth remembering.</summary>
public sealed record DialInSeries(string MethodCode, IReadOnlyList<DialInPoint> Points, DialInPoint? Best)
{
    public decimal MinGrind => Points.Min(p => p.Grind);

    public decimal MaxGrind => Points.Max(p => p.Grind);

    /// <summary>The last three brews used (nearly) the same grind: the recipe has settled.</summary>
    public bool Settled => Points.Count >= 3 && LastThree().Max() - LastThree().Min() <= Spread;

    private IEnumerable<decimal> LastThree() => Points.Skip(Points.Count - 3).Select(p => p.Grind);

    private decimal Spread => Math.Max((MaxGrind - MinGrind) / 10m, 0m);
}

/// <summary>
/// How the grind setting moved from brew to brew for one bean, per method, so you can see where you converged. Only brews with a grind setting count;
/// a method needs at least <see cref="MinPoints"/> of them to make a trace.
/// </summary>
public static class DialInTrace
{
    public const int MinPoints = 3;

    public static IReadOnlyList<DialInSeries> Build(IEnumerable<Brew> brews)
    {
        ArgumentNullException.ThrowIfNull(brews);

        return
        [
            .. brews.Where(b => !b.Deleted && b.GrindSetting is not null)
                .GroupBy(b => b.MethodCode, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= MinPoints)
                .Select(g =>
                {
                    var points = g.OrderBy(b => b.BrewedAt)
                        .Select(b => new DialInPoint(b.Id, b.BrewedAt, b.GrindSetting!.Value, b.TimeS, b.Rating))
                        .ToList();
                    // The best-rated brew; among equals the most recent one, because that is the one you would repeat.
                    var best = points.Where(p => p.Rating is not null).OrderByDescending(p => p.Rating).ThenByDescending(p => p.At).FirstOrDefault();
                    return new DialInSeries(g.First().MethodCode, points, best);
                })
                .OrderByDescending(s => s.Points.Count)
                .ThenBy(s => s.MethodCode, StringComparer.OrdinalIgnoreCase),
        ];
    }
}
