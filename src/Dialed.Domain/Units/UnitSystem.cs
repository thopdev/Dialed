namespace Dialed.Domain.Units;

/// <summary>How numbers are shown and typed. Everything is stored in SI (grams, millilitres, °C); this only changes the display.</summary>
public enum UnitSystem
{
    /// <summary>Grams, millilitres, °C (default).</summary>
    Metric,

    /// <summary>Ounces, US fluid ounces, °F.</summary>
    Imperial,
}
