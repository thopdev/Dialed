namespace Dialed.Domain.Units;

/// <summary>Conversions between the stored SI values and imperial display values. Pure maths, no rounding.</summary>
public static class UnitConversion
{
    public const decimal GramsPerOunce = 28.349523125m;
    public const decimal MillilitresPerFluidOunce = 29.5735295625m;

    public static decimal GramsToOunces(decimal grams) => grams / GramsPerOunce;

    public static decimal OuncesToGrams(decimal ounces) => ounces * GramsPerOunce;

    public static decimal MillilitresToFluidOunces(decimal millilitres) => millilitres / MillilitresPerFluidOunce;

    public static decimal FluidOuncesToMillilitres(decimal fluidOunces) => fluidOunces * MillilitresPerFluidOunce;

    public static decimal CelsiusToFahrenheit(decimal celsius) => (celsius * 9m / 5m) + 32m;

    public static decimal FahrenheitToCelsius(decimal fahrenheit) => (fahrenheit - 32m) * 5m / 9m;
}
