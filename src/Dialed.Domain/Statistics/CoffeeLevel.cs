namespace Dialed.Domain.Statistics;

/// <summary>
/// A title for how many brews you have logged, from "Beginner" to "Master". Derived at read time from the brew count: nothing is stored,
/// nothing expires and a level never goes down while the brews are there.
/// </summary>
public sealed record CoffeeLevel(int Index, int Brews, int? NextAt)
{
    /// <summary>The brew count each level starts at; the index into this list is the level.</summary>
    public static IReadOnlyList<int> Thresholds { get; } = [0, 10, 50, 100, 250, 500];

    public bool IsTop => NextAt is null;

    public int ToGo => NextAt is { } next ? Math.Max(next - Brews, 0) : 0;

    /// <summary>0 to 1: how far you are from this level's start to the next one (1 at the top).</summary>
    public double Fraction
    {
        get
        {
            if (NextAt is not { } next)
            {
                return 1;
            }

            var start = Thresholds[Index];
            return Math.Clamp((double)(Brews - start) / (next - start), 0, 1);
        }
    }

    public static CoffeeLevel For(int brews)
    {
        var count = Math.Max(brews, 0);
        var index = 0;
        for (var i = 0; i < Thresholds.Count; i++)
        {
            if (count >= Thresholds[i])
            {
                index = i;
            }
        }

        return new CoffeeLevel(index, count, index + 1 < Thresholds.Count ? Thresholds[index + 1] : null);
    }
}
