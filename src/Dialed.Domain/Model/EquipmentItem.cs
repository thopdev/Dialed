using Dialed.Domain.Equipment;

namespace Dialed.Domain.Model;

public enum EquipmentKind
{
    Grinder,
    Brewer,
    EspressoMachine,
    Kettle,
    Scale,
    Filter,
    Water,
}

/// <summary>A piece of gear (named EquipmentItem because Dialed.Domain.Equipment is a namespace). Grinders carry the scale their setting is entered on.</summary>
public sealed record EquipmentItem : SyncEntity
{
    public required EquipmentKind Kind { get; init; }

    public required string Brand { get; init; }

    public string? Model { get; init; }

    /// <summary>Only for grinders.</summary>
    public GrindScale? GrindScale { get; init; }

    public string? Notes { get; init; }

    /// <summary>Optional upkeep: service (descale, clean, replace burrs) after this many brews since <see cref="ServicedOn"/>.</summary>
    public int? ServiceEveryBrews { get; init; }

    /// <summary>Optional upkeep: service after this many kilograms of coffee since <see cref="ServicedOn"/>.</summary>
    public int? ServiceEveryKg { get; init; }

    /// <summary>When it was last serviced; usage is counted after this date. Without a date, everything ever brewed counts.</summary>
    public DateOnly? ServicedOn { get; init; }

    public IReadOnlyList<string> PhotoIds { get; init; } = [];
}
