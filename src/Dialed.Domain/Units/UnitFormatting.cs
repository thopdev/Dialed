using System.Globalization;

namespace Dialed.Domain.Units;

/// <summary>
/// Shows stored SI values in the user's unit system and turns typed values back into SI.
/// Display rounding keeps what people read in recipes: 18 g, 93 °C, 0.63 oz, 199 °F.
/// </summary>
public static class UnitFormatting
{
    // ---- Weight ----------------------------------------------------------------------------

    public static string WeightLabel(UnitSystem system) => system == UnitSystem.Imperial ? "oz" : "g";

    /// <summary>Stored grams as the number shown in an input (g with 1 decimal, oz with 2).</summary>
    public static decimal ToDisplayWeight(decimal grams, UnitSystem system) => system == UnitSystem.Imperial
        ? Math.Round(UnitConversion.GramsToOunces(grams), 2, MidpointRounding.AwayFromZero)
        : Math.Round(grams, 1, MidpointRounding.AwayFromZero);

    /// <summary>A number typed in the user's unit as stored grams (rounded to 0.1 g, the precision of a coffee scale).</summary>
    public static decimal FromDisplayWeight(decimal value, UnitSystem system) => system == UnitSystem.Imperial
        ? Math.Round(UnitConversion.OuncesToGrams(value), 1, MidpointRounding.AwayFromZero)
        : value;

    public static string FormatWeight(decimal grams, UnitSystem system) =>
        $"{Number(ToDisplayWeight(grams, system))} {WeightLabel(system)}";

    // ---- Volume ----------------------------------------------------------------------------

    public static string VolumeLabel(UnitSystem system) => system == UnitSystem.Imperial ? "fl oz" : "ml";

    public static decimal ToDisplayVolume(decimal millilitres, UnitSystem system) => system == UnitSystem.Imperial
        ? Math.Round(UnitConversion.MillilitresToFluidOunces(millilitres), 1, MidpointRounding.AwayFromZero)
        : Math.Round(millilitres, 0, MidpointRounding.AwayFromZero);

    public static decimal FromDisplayVolume(decimal value, UnitSystem system) => system == UnitSystem.Imperial
        ? Math.Round(UnitConversion.FluidOuncesToMillilitres(value), 0, MidpointRounding.AwayFromZero)
        : value;

    public static string FormatVolume(decimal millilitres, UnitSystem system) =>
        $"{Number(ToDisplayVolume(millilitres, system))} {VolumeLabel(system)}";

    // ---- Temperature -----------------------------------------------------------------------

    public static string TemperatureLabel(UnitSystem system) => system == UnitSystem.Imperial ? "°F" : "°C";

    public static decimal ToDisplayTemperature(decimal celsius, UnitSystem system) => system == UnitSystem.Imperial
        ? Math.Round(UnitConversion.CelsiusToFahrenheit(celsius), 0, MidpointRounding.AwayFromZero)
        : Math.Round(celsius, 1, MidpointRounding.AwayFromZero);

    public static decimal FromDisplayTemperature(decimal value, UnitSystem system) => system == UnitSystem.Imperial
        ? Math.Round(UnitConversion.FahrenheitToCelsius(value), 1, MidpointRounding.AwayFromZero)
        : value;

    public static string FormatTemperature(decimal celsius, UnitSystem system) =>
        $"{Number(ToDisplayTemperature(celsius, system))} {TemperatureLabel(system)}";

    /// <summary>Invariant, without trailing zeros: 18 not 18.0, 0.63 not 0.630.</summary>
    private static string Number(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
