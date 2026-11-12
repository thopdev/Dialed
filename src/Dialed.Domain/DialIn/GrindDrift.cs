using Dialed.Domain.Equipment;
using Dialed.Domain.Model;

namespace Dialed.Domain.DialIn;

/// <summary>
/// Which way the grind setting of a bag has been moving, and where the trend points today.
/// <see cref="Suggested"/> is null when the trend would not move the setting by a whole step yet.
/// </summary>
public sealed record GrindDriftNote(bool Finer, decimal From, decimal To, int Days, int Brews, decimal? Suggested);

/// <summary>
/// Spots a grind that keeps moving as a bag ages (beans degas and go stale, so people grind finer to keep the same taste).
/// Only brews of the same bag, method and grinder are compared, since settings of different grinders or methods do not mix.
/// </summary>
public static class GrindDrift
{
    public const int MinBrews = 3;
    public const int MinDays = 7;

    /// <summary>The drift for a bag, or null when there is too little data, the setting went back and forth, or it did not really move.</summary>
    public static GrindDriftNote? Detect(string bagId, IEnumerable<Brew> brews, GrindScale? scale, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(brews);
        var withSetting = brews
            .Where(b => !b.Deleted && b.BagId == bagId && b.GrindSetting is not null)
            .OrderBy(b => b.BrewedAt)
            .ToList();
        if (withSetting.Count == 0)
        {
            return null;
        }

        // The latest brew decides which method and grinder this is about.
        var newest = withSetting[^1];
        var series = withSetting.Where(b => b.MethodCode == newest.MethodCode && b.GrinderId == newest.GrinderId).ToList();
        if (series.Count < MinBrews)
        {
            return null;
        }

        var first = series[0];
        var last = series[^1];
        var days = (int)(DateOnly.FromDateTime(last.BrewedAt.ToLocalTime().DateTime).DayNumber - DateOnly.FromDateTime(first.BrewedAt.ToLocalTime().DateTime).DayNumber);
        if (days < MinDays)
        {
            return null;
        }

        var from = first.GrindSetting!.Value;
        var to = last.GrindSetting!.Value;
        var net = to - from;
        var minMove = scale is null ? 0m : scale.IsStepped ? scale.Step : (scale.Max - scale.Min) / 50m;
        if (net == 0m || Math.Abs(net) < minMove)
        {
            return null;
        }

        // Going back and forth is not a drift: every step has to go the same way (or stay).
        var sign = Math.Sign(net);
        for (var i = 1; i < series.Count; i++)
        {
            if (Math.Sign(series[i].GrindSetting!.Value - series[i - 1].GrindSetting!.Value) is { } s && s != 0 && s != sign)
            {
                return null;
            }
        }

        var sinceLast = Math.Max(0, today.DayNumber - DateOnly.FromDateTime(last.BrewedAt.ToLocalTime().DateTime).DayNumber);
        var trend = to + (net / days * sinceLast);
        var target = scale?.Snap(trend) ?? Math.Round(trend, 1, MidpointRounding.AwayFromZero);
        var suggested = target != to && Math.Abs(target - to) >= minMove ? target : (decimal?)null;
        return new GrindDriftNote(net < 0m, from, to, days, series.Count, suggested);
    }
}
