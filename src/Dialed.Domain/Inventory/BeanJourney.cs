using Dialed.Domain.Model;

namespace Dialed.Domain.Inventory;

public enum JourneyKind
{
    Purchased,
    Roasted,
    PeakStarts,
    PeakEnds,
    FirstBrew,
    Finished,
}

/// <summary>One step in the life of a bag of a bean. <see cref="Upcoming"/> steps are still ahead of today (the peak window of a bag you have not reached yet).</summary>
public sealed record JourneyEvent(DateOnly Date, JourneyKind Kind, string BagId, decimal BagWeightG, bool Upcoming);

/// <summary>
/// The timeline of one bean: for each of its bags when it was bought and roasted, when the best window starts and ends (from the same rest windows as the
/// freshness rules), the first brew, and for a finished bag its last brew. Pure: pass the data and today's date.
/// </summary>
public static class BeanJourney
{
    public static IReadOnlyList<JourneyEvent> Build(Bean bean, IEnumerable<Bag> bags, IEnumerable<Brew> brews, InventorySettings settings, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(bean);
        ArgumentNullException.ThrowIfNull(bags);
        ArgumentNullException.ThrowIfNull(brews);
        ArgumentNullException.ThrowIfNull(settings);

        var window = settings.RestWindows.For(bean.RoastLevel);
        var brewsByBag = brews.Where(b => !b.Deleted).GroupBy(b => b.BagId).ToDictionary(g => g.Key, g => g.Select(b => DateOnly.FromDateTime(b.BrewedAt.LocalDateTime)).Order().ToList());
        var events = new List<JourneyEvent>();

        foreach (var bag in bags.Where(b => !b.Deleted && b.BeanId == bean.Id))
        {
            void Add(DateOnly? date, JourneyKind kind)
            {
                if (date is { } d)
                {
                    events.Add(new JourneyEvent(d, kind, bag.Id, bag.WeightG, d > today));
                }
            }

            Add(bag.PurchasedOn, JourneyKind.Purchased);
            Add(bag.RoastDate, JourneyKind.Roasted);
            if (bag.RoastDate is { } roasted)
            {
                var rest = bag.RestDaysOverride ?? window.RestDays;
                Add(roasted.AddDays(rest), JourneyKind.PeakStarts);
                Add(roasted.AddDays(Math.Max(rest, window.PeakEndDays)), JourneyKind.PeakEnds);
            }

            if (brewsByBag.TryGetValue(bag.Id, out var days) && days.Count > 0)
            {
                Add(days[0], JourneyKind.FirstBrew);
                if (bag.Status == BagStatus.Finished)
                {
                    Add(days[^1], JourneyKind.Finished);
                }
            }
        }

        return [.. events.OrderBy(e => e.Date).ThenBy(e => e.Kind).ThenBy(e => e.BagId, StringComparer.Ordinal)];
    }
}
