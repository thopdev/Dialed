using Dialed.Client.Preferences;
using Dialed.Domain.Units;

namespace Dialed.Client.Tests;

public sealed class UnitFormatterTests
{
    [Fact]
    public async Task Formatter_follows_the_preference_when_it_changes()
    {
        var preferences = new InMemoryUserPreferences();
        var formatter = new UnitFormatter(preferences);

        formatter.Weight(18m).ShouldBe("18 g");
        formatter.Temperature(93m).ShouldBe("93 °C");

        await preferences.SetUnitsAsync(UnitSystem.Imperial);

        formatter.Weight(18m).ShouldBe("0.63 oz");
        formatter.Temperature(93m).ShouldBe("199 °F");
        formatter.WeightLabel.ShouldBe("oz");
        formatter.FromDisplayWeight(0.63m).ShouldBe(17.9m);
        formatter.ToDisplayTemperature(93m).ShouldBe(199m);
        formatter.FromDisplayTemperature(199m).ShouldBe(92.8m);
        formatter.Volume(296m).ShouldBe("10 fl oz");
        formatter.VolumeLabel.ShouldBe("fl oz");
        formatter.TemperatureLabel.ShouldBe("°F");
        formatter.ToDisplayWeight(18m).ShouldBe(0.63m);
    }

    [Fact]
    public async Task Preferences_default_to_metric_and_only_notify_on_a_real_change()
    {
        var preferences = new InMemoryUserPreferences();
        var changes = 0;
        preferences.Changed += () => changes++;

        preferences.Units.ShouldBe(UnitSystem.Metric);
        await preferences.SetUnitsAsync(UnitSystem.Metric);
        changes.ShouldBe(0);

        await preferences.SetUnitsAsync(UnitSystem.Imperial);
        changes.ShouldBe(1);
    }
}
