using Dialed.Domain.Model;

namespace Dialed.Domain.Statistics;

public enum MilestoneKind
{
    Brews,
    Origins,
    Roasters,
    Streak,
}

/// <summary>One step on a track ("10 brews"): <see cref="Progress"/> is where you are on that track, so the step is earned from <see cref="Threshold"/> on.</summary>
public sealed record Milestone(MilestoneKind Kind, int Threshold, int Progress)
{
    public bool Earned => Progress >= Threshold;

    public int ToGo => Math.Max(Threshold - Progress, 0);
}

/// <summary>Brews, origins, roasters and your longest run of days with a brew, and the milestones they reach. Quiet by design: nothing here expires or nags.</summary>
public sealed record MilestoneSummary(int Brews, int Origins, int Roasters, int CurrentStreak, int LongestStreak, IReadOnlyList<Milestone> Earned, Milestone? Next)
{
    private static readonly Dictionary<MilestoneKind, int[]> Steps = new()
    {
        [MilestoneKind.Brews] = [10, 50, 100, 250, 500, 1000],
        [MilestoneKind.Origins] = [3, 5, 10, 20],
        [MilestoneKind.Roasters] = [3, 5, 10],
        [MilestoneKind.Streak] = [3, 7, 14, 30, 100],
    };

    public static MilestoneSummary Calculate(IEnumerable<Brew> brews, IEnumerable<Bag> bags, IEnumerable<Bean> beans, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(brews);
        ArgumentNullException.ThrowIfNull(bags);
        ArgumentNullException.ThrowIfNull(beans);

        var live = brews.Where(b => !b.Deleted).ToList();
        var bagById = bags.Where(b => !b.Deleted).ToDictionary(b => b.Id);
        var beanById = beans.Where(b => !b.Deleted).ToDictionary(b => b.Id);
        var brewed = live.Select(b => bagById.GetValueOrDefault(b.BagId)).OfType<Bag>().Select(bag => beanById.GetValueOrDefault(bag.BeanId)).OfType<Bean>().DistinctBy(b => b.Id).ToList();

        var origins = brewed.Select(b => b.Origin.Country?.Trim()).Where(c => !string.IsNullOrEmpty(c)).Select(c => Geography.CountryCatalog.Find(c)?.Iso2 ?? c!).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var roasters = brewed.Select(b => b.RoasterId).Where(id => !string.IsNullOrEmpty(id)).Distinct().Count();
        var days = live.Select(b => DateOnly.FromDateTime(b.BrewedAt.LocalDateTime)).Distinct().Order().ToList();
        var (current, longest) = Streaks(days, today);

        var progress = new Dictionary<MilestoneKind, int>
        {
            [MilestoneKind.Brews] = live.Count,
            [MilestoneKind.Origins] = origins,
            [MilestoneKind.Roasters] = roasters,
            [MilestoneKind.Streak] = longest,
        };
        var all = Steps.SelectMany(s => s.Value.Select(threshold => new Milestone(s.Key, threshold, progress[s.Key]))).ToList();
        var earned = all.Where(m => m.Earned).OrderBy(m => m.Kind).ThenBy(m => m.Threshold).ToList();
        // Per track only the highest step earned is worth showing; the next one is the closest step you have not reached.
        var shown = earned.GroupBy(m => m.Kind).Select(g => g.Last()).ToList();
        var next = all.Where(m => !m.Earned).OrderBy(m => (double)m.ToGo / m.Threshold).ThenBy(m => m.Kind).FirstOrDefault();
        return new MilestoneSummary(live.Count, origins, roasters, current, longest, shown, next);
    }

    /// <summary>The run of days up to today (or yesterday: today may not have a brew yet) and the longest run ever.</summary>
    private static (int Current, int Longest) Streaks(IReadOnlyList<DateOnly> days, DateOnly today)
    {
        var longest = 0;
        var run = 0;
        DateOnly? previous = null;
        foreach (var day in days)
        {
            run = previous is { } p && day == p.AddDays(1) ? run + 1 : 1;
            longest = Math.Max(longest, run);
            previous = day;
        }

        var current = previous is { } last && (last == today || last == today.AddDays(-1)) ? run : 0;
        return (current, longest);
    }
}
