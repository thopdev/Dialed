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

    /// <summary>When the current freeze started; <c>null</c> unless the bag is frozen right now.</summary>
    public DateOnly? FrozenOn { get; init; }

    /// <summary>Days spent frozen in earlier freezes. Frozen days do not count toward the bag's age.</summary>
    public int FrozenDays { get; init; }

    public DateOnly? FinishedOn { get; init; }

    public BagStorage Storage { get; init; } = BagStorage.Pantry;

    public BagStatus Status { get; init; } = BagStatus.Sealed;

    /// <summary>Rest days before the coffee is at its best, overriding the default for the roast style.</summary>
    public int? RestDaysOverride { get; init; }
}
