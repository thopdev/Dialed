using Dialed.Domain.Model;

namespace Dialed.Domain.Inventory;

public enum RoastAgePosition
{
    /// <summary>The bag is younger than your best brews of this bean were.</summary>
    Before,
    Inside,
    /// <summary>The bag is older than your best brews of this bean were.</summary>
    After,
}

/// <summary>The ages (days after roasting) at which a bean did best for you, and where a bag is now.</summary>
public sealed record RoastAgeTipResult(int FromDays, int ToDays, int MedianDays, int Brews, RoastAgePosition Position, int DaysAway);

/// <summary>
/// Looks at a bean's brews rated 4 or more and how old the bag was when each was made (frozen time does not count, as everywhere), and says
/// which age range your best brews came from and whether the bag in hand is inside it. Needs at least three such brews with a known roast
/// date, across all bags of the bean. A hint: it only reports what happened, it does not know why. Pure.
/// </summary>
public static class RoastAgeTip
{
    public const int MinBrews = 3;

    public static RoastAgeTipResult? Build(string beanId, IEnumerable<Bag> bags, IEnumerable<Brew> brews, Bag current, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(beanId);
        ArgumentNullException.ThrowIfNull(bags);
        ArgumentNullException.ThrowIfNull(brews);
        ArgumentNullException.ThrowIfNull(current);

        var beanBags = bags.Where(b => !b.Deleted && b.BeanId == beanId).ToDictionary(b => b.Id);
        var ages = brews
            .Where(b => !b.Deleted && b.Rating >= 4 && beanBags.ContainsKey(b.BagId))
            .Select(b => BagLifecycle.AgeDays(beanBags[b.BagId], DateOnly.FromDateTime(b.BrewedAt.LocalDateTime)))
            .OfType<int>()
            .Order()
            .ToList();
        if (ages.Count < MinBrews || BagLifecycle.AgeDays(current, today) is not { } now)
        {
            return null;
        }

        var median = ages[ages.Count / 2];
        var position = now < ages[0] ? RoastAgePosition.Before : now > ages[^1] ? RoastAgePosition.After : RoastAgePosition.Inside;
        var away = position == RoastAgePosition.Before ? ages[0] - now : position == RoastAgePosition.After ? now - ages[^1] : 0;
        return new RoastAgeTipResult(ages[0], ages[^1], median, ages.Count, position, away);
    }
}
