using Bunit;
using Dialed.Client.Preferences;
using Dialed.Domain.Units;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class UnitsSettingTests : UiTestContext
{
    [Fact]
    public void Metric_is_selected_by_default()
    {
        var setting = Render<UnitsSetting>();

        setting.Find("input[value=metric]").HasAttribute("checked").ShouldBeTrue();
        setting.Find("input[value=imperial]").HasAttribute("checked").ShouldBeFalse();
    }

    [Fact]
    public void Choosing_imperial_changes_the_preference()
    {
        var preferences = Services.GetRequiredService<IUserPreferences>();
        var setting = Render<UnitsSetting>();

        setting.Find("input[value=imperial]").Change("imperial");

        preferences.Units.ShouldBe(UnitSystem.Imperial);
        setting.WaitForAssertion(() => setting.Find("input[value=imperial]").HasAttribute("checked").ShouldBeTrue());
    }

    [Fact]
    public void Settings_page_offers_the_units_setting()
    {
        Render<Settings>(p => p.Add(x => x.Group, "preferences")).FindAll(".dialed-units-setting").Count.ShouldBe(1);
    }
}
