namespace Dialed.Domain.Model;

public enum DrinkType
{
    Espresso,
    Ristretto,
    Lungo,
    Americano,
    Cortado,
    Cappuccino,
    FlatWhite,
    Latte,
    Filter,
    ColdBrew,
    Other,
}

public enum MilkType
{
    Whole,
    SemiSkimmed,
    Skimmed,
    Oat,
    Soy,
    Almond,
    Other,
}

/// <summary>How a brew tasted, each from -2 to +2 with 0 balanced: acidity (sour to bitter), strength (weak to strong), body (thin to heavy), sweetness (flat to sweet).</summary>
public sealed record Taste(int Acidity = 0, int Strength = 0, int Body = 0, int Sweetness = 0)
{
    public const int Min = -2;

    public const int Max = 2;

    public bool IsBalanced => this == new Taste();
}

/// <summary>
/// One brewed coffee. Append-mostly: a brew is only ever added (and later edited by its author), so two people
/// brewing from the same bag offline never overwrite each other. Its <see cref="DoseG"/> counts toward the bag's consumption.
/// </summary>
public sealed record Brew : SyncEntity
{
    public required string BagId { get; init; }

    public required string MethodCode { get; init; }

    public string? RecipeId { get; init; }

    public string? GrinderId { get; init; }

    public DateTimeOffset BrewedAt { get; init; }

    public DrinkType DrinkType { get; init; } = DrinkType.Filter;

    public int? MilkMl { get; init; }

    public MilkType? MilkType { get; init; }

    /// <summary>Actual dose in grams: what leaves the bag.</summary>
    public required decimal DoseG { get; init; }

    public decimal? YieldG { get; init; }

    public decimal? WaterG { get; init; }

    public decimal? GrindSetting { get; init; }

    public decimal? TempC { get; init; }

    public int? TimeS { get; init; }

    public Taste Taste { get; init; } = new();

    /// <summary>1 to 5, or <c>null</c> when not rated.</summary>
    public int? Rating { get; init; }

    public string? Notes { get; init; }

    public IReadOnlyList<string> PhotoIds { get; init; } = [];
}

public static class DrinkTypes
{
    public static bool UsesMilk(DrinkType type) => type is DrinkType.Cortado or DrinkType.Cappuccino or DrinkType.FlatWhite or DrinkType.Latte;

    /// <summary>The drink a brew method usually makes, to prefill the form.</summary>
    public static DrinkType DefaultFor(string methodCode) => methodCode switch
    {
        "espresso" => DrinkType.Espresso,
        "cold-brew" => DrinkType.ColdBrew,
        "moka-pot" or "cezve" => DrinkType.Other,
        _ => DrinkType.Filter,
    };
}
