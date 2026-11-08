using Dialed.Domain.Model;

namespace Dialed.Domain.Inventory;

public enum Freshness
{
    Unknown,
    Resting,
    Peak,
    Past,
}

/// <summary>Days after roasting when the coffee is ready (<see cref="RestDays"/>) and when it starts to fade (<see cref="PeakEndDays"/>).</summary>
public sealed record RestWindow(int RestDays, int PeakEndDays);

/// <summary>Rest/peak windows for light, medium and dark roasts; the defaults can be changed in settings.</summary>
public sealed record RestWindows(RestWindow Light, RestWindow Medium, RestWindow Dark)
{
    public static RestWindows Default { get; } = new(new RestWindow(10, 30), new RestWindow(7, 21), new RestWindow(5, 14));

    /// <summary>Roast level to window: light and medium-light share one, medium is alone, medium-dark and dark share one. Unknown counts as medium.</summary>
    public RestWindow For(RoastLevel? level) => level switch
    {
        RoastLevel.Light or RoastLevel.MediumLight => Light,
        RoastLevel.MediumDark or RoastLevel.Dark => Dark,
        _ => Medium,
    };
}

public static class FreshnessRules
{
    /// <summary>Where a bag is in its life. A bag's own rest days override the window's; the peak never ends before the rest does.</summary>
    public static Freshness Classify(int? ageDays, RestWindow window, int? restDaysOverride = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (ageDays is not { } age)
        {
            return Freshness.Unknown;
        }

        var rest = restDaysOverride ?? window.RestDays;
        var peakEnd = Math.Max(rest, window.PeakEndDays);
        return age < rest ? Freshness.Resting : age <= peakEnd ? Freshness.Peak : Freshness.Past;
    }
}
