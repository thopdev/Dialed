using Dialed.Domain.Model;

namespace Dialed.Domain.Inventory;

public enum StockAlertKind
{
    LowStock,
    Peak,
}

/// <summary>
/// What a person was already told about, so a bag is announced once. A bag drops out when it stops being a candidate (topped up, finished,
/// left its peak window), which re-arms it. Keys are <c>householdId/bagId</c>.
/// </summary>
public sealed record StockAlertMemory(IReadOnlySet<string> Low, IReadOnlySet<string> Peak)
{
    public static StockAlertMemory Empty { get; } = new(new HashSet<string>(), new HashSet<string>());

    /// <summary>Bags already told "peak starts tomorrow". Kept apart from <see cref="Peak"/> so the heads-up and the peak itself are each sent once.</summary>
    public IReadOnlySet<string> PeakSoon { get; init; } = new HashSet<string>();
}

/// <summary>The bags to tell a person about now, and what to remember for next time.</summary>
public sealed record StockAlertResult(IReadOnlyList<string> LowStock, IReadOnlyList<string> Peak, StockAlertMemory Memory)
{
    /// <summary>Beans whose peak window starts tomorrow (a heads-up, sent under the same switch as the peak alert).</summary>
    public IReadOnlyList<string> PeakSoon { get; init; } = [];
}

/// <summary>
/// Decides which bags deserve a notification (#259), from the same <see cref="InventoryOverview"/> the app shows, so the server and the app
/// cannot disagree about what "low" or "peak" means. Pure: the worker supplies the bags and the clock.
/// </summary>
public static class StockAlerts
{
    /// <summary>
    /// A bag is a low-stock candidate when it is open and under the threshold; a peak candidate when it is not finished, frozen or sealed
    /// away and its roast date puts it inside its best window. Bags without what the alert needs (no roast date for a peak) are skipped.
    /// </summary>
    public static StockAlertResult Evaluate(string householdId, IEnumerable<BagStock> stock, StockAlertMemory previous, bool lowStock, bool peak)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(previous);

        var bags = stock.Where(s => !s.Bag.Deleted).ToList();
        var lowNow = lowStock
            ? bags.Where(s => s.Bag.Status == BagStatus.Open && s.IsLow).ToList()
            : [];
        var peakNow = peak
            ? bags.Where(s => s.Bag.Status is BagStatus.Open or BagStatus.Sealed && s.Bag.RoastDate is not null && s.Freshness == Freshness.Peak).ToList()
            : [];

        var soonNow = peak
            ? bags.Where(s => s.Bag.Status is BagStatus.Open or BagStatus.Sealed && s.Bag.RoastDate is not null && s.PeakTomorrow).ToList()
            : [];

        string Key(BagStock s) => $"{householdId}/{s.Bag.Id}";

        // Only this household's keys are replaced; other households' memory is carried over untouched.
        var low = Merge(previous.Low, householdId, lowNow.Select(Key));
        var peakKeys = Merge(previous.Peak, householdId, peakNow.Select(Key));
        var soonKeys = Merge(previous.PeakSoon, householdId, soonNow.Select(Key));

        return new StockAlertResult(
            Names(lowNow.Where(s => !previous.Low.Contains(Key(s)))),
            Names(peakNow.Where(s => !previous.Peak.Contains(Key(s)))),
            new StockAlertMemory(low, peakKeys) { PeakSoon = soonKeys })
        {
            PeakSoon = Names(soonNow.Where(s => !previous.PeakSoon.Contains(Key(s)))),
        };
    }

    private static IReadOnlySet<string> Merge(IReadOnlySet<string> before, string householdId, IEnumerable<string> now)
    {
        var prefix = householdId + "/";
        var kept = before.Where(k => !k.StartsWith(prefix, StringComparison.Ordinal));
        return new HashSet<string>(kept.Concat(now), StringComparer.Ordinal);
    }

    private static IReadOnlyList<string> Names(IEnumerable<BagStock> bags) =>
        [.. bags.Select(s => string.IsNullOrWhiteSpace(s.BeanName) ? "?" : s.BeanName).Order(StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>
    /// Whether it is time to look at a person's bags: their local time is between <paramref name="from"/> and <paramref name="until"/> and they
    /// have not been handled on their local date yet (about 08:00, never at night).
    /// </summary>
    public static bool IsDue(DateTimeOffset utcNow, TimeZoneInfo zone, DateOnly? lastHandled, int from = 8, int until = 21)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var local = TimeZoneInfo.ConvertTime(utcNow, zone);
        return local.Hour >= from && local.Hour < until && lastHandled != DateOnly.FromDateTime(local.DateTime);
    }

    /// <summary>The person's local date at <paramref name="utcNow"/>.</summary>
    public static DateOnly LocalDate(DateTimeOffset utcNow, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, zone).DateTime);
}
