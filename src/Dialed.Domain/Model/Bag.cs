using Dialed.Domain.Inventory;

namespace Dialed.Domain.Model;

public enum BagStorage
{
    Pantry,
    Fridge,
    Freezer,
}

/// <summary>One purchase of a bean. How much is left is calculated (<see cref="BagInventory"/>), never stored.</summary>
public sealed record Bag : SyncEntity
{
    public required string BeanId { get; init; }

    public string? SellerId { get; init; }

    public int? PriceCents { get; init; }

    /// <summary>ISO 4217, for example EUR.</summary>
    public string? Currency { get; init; }

    public required decimal WeightG { get; init; }

    public DateOnly? RoastDate { get; init; }

    public DateOnly? PurchasedOn { get; init; }

    public DateOnly? OpenedOn { get; init; }

    public DateOnly? FrozenOn { get; init; }

    public BagStorage Storage { get; init; } = BagStorage.Pantry;

    public BagStatus Status { get; init; } = BagStatus.Sealed;

    /// <summary>Rest days before the coffee is at its best, overriding the default for the roast style.</summary>
    public int? RestDaysOverride { get; init; }
}
