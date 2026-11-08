using Dialed.Domain.Model;

namespace Dialed.Domain.Inventory;

/// <summary>
/// The steps in a bag's life: sealed, opened, frozen, thawed, finished. Each step returns a changed copy.
/// A step that makes no sense (thawing a bag that is not frozen, a date before the freeze started) returns <c>false</c>.
/// </summary>
public static class BagLifecycle
{
    public static bool CanOpen(Bag bag) => bag.Status == BagStatus.Sealed;

    public static bool CanFreeze(Bag bag) => bag.Status is BagStatus.Sealed or BagStatus.Open;

    public static bool CanThaw(Bag bag) => bag.Status == BagStatus.Frozen;

    public static bool CanFinish(Bag bag) => bag.Status != BagStatus.Finished;

    public static bool TryOpen(Bag bag, DateOnly date, out Bag result)
    {
        result = bag;
        if (!CanOpen(bag))
        {
            return false;
        }

        result = bag with { Status = BagStatus.Open, OpenedOn = date };
        return true;
    }

    public static bool TryFreeze(Bag bag, DateOnly date, out Bag result)
    {
        result = bag;
        if (!CanFreeze(bag))
        {
            return false;
        }

        result = bag with { Status = BagStatus.Frozen, FrozenOn = date, Storage = BagStorage.Freezer };
        return true;
    }

    /// <summary>Back out of the freezer: the days spent frozen are added up and stop counting toward age.</summary>
    public static bool TryThaw(Bag bag, DateOnly date, out Bag result)
    {
        result = bag;
        if (!CanThaw(bag) || bag.FrozenOn is not { } since || date < since)
        {
            return false;
        }

        result = bag with
        {
            Status = bag.OpenedOn is null ? BagStatus.Sealed : BagStatus.Open,
            FrozenOn = null,
            FrozenDays = bag.FrozenDays + date.DayNumber - since.DayNumber,
            Storage = BagStorage.Pantry,
        };
        return true;
    }

    public static bool TryFinish(Bag bag, DateOnly date, out Bag result)
    {
        result = bag;
        if (!CanFinish(bag))
        {
            return false;
        }

        if (bag.Status == BagStatus.Frozen)
        {
            if (!TryThaw(bag, date, out var thawed))
            {
                return false;
            }

            bag = thawed;
        }

        result = bag with { Status = BagStatus.Finished, FinishedOn = date };
        return true;
    }

    /// <summary>
    /// Days since roasting, not counting days in the freezer (earlier freezes and the one that is going on now).
    /// <c>null</c> when the roast date is unknown.
    /// </summary>
    public static int? AgeDays(Bag bag, DateOnly today)
    {
        if (bag.RoastDate is not { } roasted)
        {
            return null;
        }

        var end = bag.FinishedOn is { } finished && finished < today ? finished : today;
        var frozenNow = bag.Status == BagStatus.Frozen && bag.FrozenOn is { } since ? Math.Max(0, end.DayNumber - since.DayNumber) : 0;
        return Math.Max(0, end.DayNumber - roasted.DayNumber - bag.FrozenDays - frozenNow);
    }

    /// <summary>A copy for "bought again": same bean, seller, price and weight, bought today, sealed and not yet roast-dated.</summary>
    public static Bag BoughtAgain(Bag last, DateOnly today) => new()
    {
        BeanId = last.BeanId,
        SellerId = last.SellerId,
        PriceCents = last.PriceCents,
        Currency = last.Currency,
        WeightG = last.WeightG,
        Storage = BagStorage.Pantry,
        PurchasedOn = today,
        RestDaysOverride = last.RestDaysOverride,
        Status = BagStatus.Sealed,
    };
}
