using Bunit;
using Dialed.UI.Components;

namespace Dialed.UI.Tests;

public sealed class OriginMapTests : UiTestContext
{
    [Fact]
    public void Shows_a_marker_for_each_known_country_and_names_them_for_screen_readers()
    {
        var map = Render<OriginMap>(p => p.Add(m => m.Origins, [("Ethiopia", 3), ("kenya", 1), ("Ethiopië", 2), ("Atlantis", 5)]));

        map.FindAll("circle").Count.ShouldBe(2); // both Ethiopia spellings join one marker; Atlantis is unknown
        var label = map.Find("svg").GetAttribute("aria-label")!;
        label.ShouldContain("Ethiopia");
        label.ShouldContain("kenya");
        label.ShouldNotContain("Atlantis");
        map.Find("svg").GetAttribute("role").ShouldBe("img");
    }

    [Fact]
    public void Shows_nothing_without_a_known_country()
    {
        Render<OriginMap>(p => p.Add(m => m.Origins, [("Blend", 1)])).Markup.Trim().ShouldBeEmpty();
        Render<OriginMap>(p => p.Add(m => m.Origins, [])).Markup.Trim().ShouldBeEmpty();
    }

    [Fact]
    public void A_busier_country_gets_a_bigger_marker()
    {
        var map = Render<OriginMap>(p => p.Add(m => m.Origins, [("Ethiopia", 9), ("Kenya", 1)]));

        var radii = map.FindAll("circle").Select(c => double.Parse(c.GetAttribute("r")!, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        radii[0].ShouldBeGreaterThan(radii[1]);
    }
}
