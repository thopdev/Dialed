namespace Dialed.Domain.Inventory;

public enum AdjustmentReason
{
    Spilled,
    Gifted,
    Correction,
    Other,
}

/// <summary>
/// A correction to a bag's inventory. Adjustments are append-only: to undo one, add another.
/// <paramref name="AmountG"/> is the coffee taken out of the bag (spilled, gifted away), so it
/// reduces what is left. A negative amount puts coffee back, which a correction needs when the count was too low.
/// </summary>
public sealed record InventoryAdjustment(string Id, string BagId, decimal AmountG, AdjustmentReason Reason, DateTimeOffset CreatedAt);
