namespace Dialed.Domain.Model;

public enum RecipeStepKind
{
    Bloom,
    Pour,
    Stir,
    Swirl,
    Press,
    Wait,
}

/// <summary>One step of a recipe timeline. <see cref="WaterG"/> is the total water poured once the step is done.</summary>
public sealed record RecipeStep(RecipeStepKind Kind, int AtS, int? DurationS = null, decimal? WaterG = null, string? Note = null);

/// <summary>
/// How to brew one method. Only the parameters the method uses are filled in. Ratio is never stored: it is
/// liquid (water, or yield for espresso) divided by dose.
/// </summary>
public sealed record Recipe : SyncEntity
{
    public required string MethodCode { get; init; }

    public required string Name { get; init; }

    public string? BeanId { get; init; }

    public string? GrinderId { get; init; }

    public string? BrewerId { get; init; }

    public decimal? DoseG { get; init; }

    public decimal? WaterG { get; init; }

    public decimal? YieldG { get; init; }

    /// <summary>On the scale of the linked grinder.</summary>
    public decimal? GrindSetting { get; init; }

    public decimal? TempC { get; init; }

    public int? TimeS { get; init; }

    public int? PreinfusionS { get; init; }

    public IReadOnlyList<RecipeStep> Steps { get; init; } = [];

    public string? Filter { get; init; }

    public string? Notes { get; init; }

    /// <summary>The version this one replaced (dial-in history, #62).</summary>
    public string? ParentRecipeId { get; init; }

    /// <summary>True for an older version that a newer one replaced; lists only show the current version of each recipe.</summary>
    public bool IsSuperseded { get; init; }

    /// <summary>A read-only starting point shipped with the app (#60).</summary>
    public bool IsDefaultTemplate { get; init; }

    public bool IsFavourite { get; init; }

    /// <summary>Set when this was copied from a friend (#77).</summary>
    public CopiedFrom? CopiedFrom { get; init; }

    public Visibility Visibility { get; init; } = Visibility.Household;

    /// <summary>Water per gram of coffee (16 means 1:16), or <c>null</c> when dose or liquid is missing.</summary>
    public decimal? Ratio
    {
        get
        {
            var liquid = WaterG ?? YieldG;
            return DoseG is > 0m && liquid is not null ? Math.Round(liquid.Value / DoseG.Value, 2) : null;
        }
    }
}
