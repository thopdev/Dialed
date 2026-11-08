using Dialed.Domain.Units;

namespace Dialed.Client.Preferences;

/// <summary>Formats and parses weights, volumes and temperatures in the unit system the user chose.</summary>
public sealed class UnitFormatter(IUserPreferences preferences)
{
    public UnitSystem System => preferences.Units;

    public string Weight(decimal grams) => UnitFormatting.FormatWeight(grams, System);

    public string Volume(decimal millilitres) => UnitFormatting.FormatVolume(millilitres, System);

    public string Temperature(decimal celsius) => UnitFormatting.FormatTemperature(celsius, System);

    public string WeightLabel => UnitFormatting.WeightLabel(System);

    public string VolumeLabel => UnitFormatting.VolumeLabel(System);

    public string TemperatureLabel => UnitFormatting.TemperatureLabel(System);

    public decimal ToDisplayWeight(decimal grams) => UnitFormatting.ToDisplayWeight(grams, System);

    public decimal FromDisplayWeight(decimal value) => UnitFormatting.FromDisplayWeight(value, System);

    public decimal ToDisplayTemperature(decimal celsius) => UnitFormatting.ToDisplayTemperature(celsius, System);

    public decimal FromDisplayTemperature(decimal value) => UnitFormatting.FromDisplayTemperature(value, System);
}
