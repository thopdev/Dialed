namespace Dialed.Domain.Model;

public enum RoastLevel
{
    Light,
    MediumLight,
    Medium,
    MediumDark,
    Dark,
}

public enum RoastStyle
{
    Espresso,
    Filter,
    Omni,
}

public sealed record BeanOrigin(string? Country = null, string? Region = null, string? Farm = null, string? Producer = null);

/// <summary>A coffee as the roaster describes it. What you bought and own is a <see cref="Bag"/>.</summary>
public sealed record Bean : SyncEntity
{
    public required string Name { get; init; }

    public string? RoasterId { get; init; }

    public BeanOrigin Origin { get; init; } = new();

    public IReadOnlyList<string> Varieties { get; init; } = [];

    public string? Process { get; init; }

    public int? AltitudeM { get; init; }

    public RoastLevel? RoastLevel { get; init; }

    public RoastStyle? RoastStyle { get; init; }

    public bool IsDecaf { get; init; }

    public IReadOnlyList<string> TastingNotes { get; init; } = [];

    public IReadOnlyList<string> PhotoIds { get; init; } = [];

    /// <summary>1 to 5.</summary>
    public int? Rating { get; init; }

    public string? Notes { get; init; }

    public Visibility Visibility { get; init; } = Visibility.Household;

    /// <summary>The label scan this bean was created from, if any (M3).</summary>
    public string? SourceScanId { get; init; }
}
