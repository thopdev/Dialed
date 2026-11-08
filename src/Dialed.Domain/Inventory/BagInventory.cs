namespace Dialed.Domain.Inventory;

public enum BagStatus
{
    Sealed,
    Open,
    Frozen,
    Finished,
}

/// <summary>
/// How much coffee is left in a bag. It is always calculated, never stored as a counter:
/// <c>remaining = weight - sum(brew doses) - sum(adjustments)</c>. Brews and adjustments are only ever added,
/// so two people brewing from the same bag offline can never overwrite each other; after a sync both are
/// simply part of the sums.
/// </summary>
public static class BagInventory
{
    /// <summary>
    /// The exact remaining weight in grams. It can be negative when more was brewed than the bag held
    /// (a wrong weight or an unlogged refill); use <see cref="Display"/> for showing it.
    /// </summary>
    public static decimal Remaining(decimal weightG, IEnumerable<decimal> brewDosesG, IEnumerable<InventoryAdjustment> adjustments)
    {
        ArgumentNullException.ThrowIfNull(brewDosesG);
        ArgumentNullException.ThrowIfNull(adjustments);

        return weightG - brewDosesG.Sum() - adjustments.Sum(adjustment => adjustment.AmountG);
    }

    /// <summary>Remaining weight as shown to the user: never below zero.</summary>
    public static decimal Display(decimal remainingG) => Math.Max(0m, remainingG);

    /// <summary>Remaining as a share of the bag, between 0 and 1 (0 for a bag without weight).</summary>
    public static decimal FractionLeft(decimal weightG, decimal remainingG) =>
        weightG <= 0m ? 0m : Math.Clamp(remainingG / weightG, 0m, 1m);

    /// <summary>The app offers to mark a bag finished once nothing is left in it and it is not finished yet.</summary>
    public static bool ShouldOfferFinish(BagStatus status, decimal remainingG) =>
        status != BagStatus.Finished && remainingG <= 0m;
}
