using Dialed.Domain.Units;

namespace Dialed.Domain.Tests;

public sealed class UnitFormattingTests
{
    [Theory]
    [InlineData(18, "18 g")]
    [InlineData(17.5, "17.5 g")]
    [InlineData(17.54, "17.5 g")]
    [InlineData(250, "250 g")]
    public void Metric_weight_keeps_one_decimal_without_trailing_zeros(decimal grams, string expected)
    {
        UnitFormatting.FormatWeight(grams, UnitSystem.Metric).ShouldBe(expected);
    }

    [Theory]
    [InlineData(18, "0.63 oz")]
    [InlineData(340, "11.99 oz")]
    [InlineData(28.349523125, "1 oz")]
    public void Imperial_weight_is_shown_in_ounces(decimal grams, string expected)
    {
        UnitFormatting.FormatWeight(grams, UnitSystem.Imperial).ShouldBe(expected);
    }

    [Theory]
    [InlineData(93, "93 °C")]
    [InlineData(92.5, "92.5 °C")]
    public void Metric_temperature_is_celsius(decimal celsius, string expected)
    {
        UnitFormatting.FormatTemperature(celsius, UnitSystem.Metric).ShouldBe(expected);
    }

    [Theory]
    [InlineData(93, "199 °F")]
    [InlineData(100, "212 °F")]
    [InlineData(0, "32 °F")]
    public void Imperial_temperature_is_fahrenheit(decimal celsius, string expected)
    {
        UnitFormatting.FormatTemperature(celsius, UnitSystem.Imperial).ShouldBe(expected);
    }

    [Theory]
    [InlineData(250, "250 ml")]
    [InlineData(296, "10 fl oz")]
    public void Volume_is_shown_in_the_chosen_system(decimal millilitres, string expected)
    {
        var system = expected.EndsWith("fl oz", StringComparison.Ordinal) ? UnitSystem.Imperial : UnitSystem.Metric;

        UnitFormatting.FormatVolume(millilitres, system).ShouldBe(expected);
    }

    [Fact]
    public void Typed_imperial_values_become_metric()
    {
        UnitFormatting.FromDisplayWeight(0.63m, UnitSystem.Imperial).ShouldBe(17.9m);
        UnitFormatting.FromDisplayTemperature(199m, UnitSystem.Imperial).ShouldBe(92.8m);
        UnitFormatting.FromDisplayVolume(10m, UnitSystem.Imperial).ShouldBe(296m);
    }

    [Fact]
    public void Metric_values_are_stored_as_typed()
    {
        UnitFormatting.FromDisplayWeight(18.2m, UnitSystem.Metric).ShouldBe(18.2m);
        UnitFormatting.FromDisplayTemperature(93m, UnitSystem.Metric).ShouldBe(93m);
        UnitFormatting.FromDisplayVolume(250m, UnitSystem.Metric).ShouldBe(250m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(18)]
    [InlineData(250.4)]
    public void Weight_survives_a_round_trip_through_ounces_within_display_precision(decimal grams)
    {
        var shown = UnitFormatting.ToDisplayWeight(grams, UnitSystem.Imperial);
        var back = UnitFormatting.FromDisplayWeight(shown, UnitSystem.Imperial);

        Math.Abs(back - grams).ShouldBeLessThanOrEqualTo(0.2m);
    }

    [Fact]
    public void Labels_follow_the_system()
    {
        UnitFormatting.WeightLabel(UnitSystem.Metric).ShouldBe("g");
        UnitFormatting.WeightLabel(UnitSystem.Imperial).ShouldBe("oz");
        UnitFormatting.VolumeLabel(UnitSystem.Imperial).ShouldBe("fl oz");
        UnitFormatting.TemperatureLabel(UnitSystem.Imperial).ShouldBe("°F");
    }
}
