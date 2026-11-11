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

    [Fact]
    public void Zoomed_on_one_country_outlines_it_instead_of_dropping_a_marker()
    {
        var map = Render<OriginMap>(p => p.Add(m => m.Origins, [("Rwanda", 1)]).Add(m => m.Zoom, true));

        map.FindAll("circle").ShouldBeEmpty();
        map.Find("path.dialed-origin-map-country").ShouldNotBeNull();
        var box = map.Find("svg").GetAttribute("viewBox")!.Split(' ').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        box[2].ShouldBeLessThan(100); // a window of a few percent of the world, not the whole 1000
        map.Find("svg").GetAttribute("aria-label")!.ShouldContain("Rwanda");
    }

    [Fact]
    public void Every_country_in_the_catalog_has_a_shape_to_zoom_to()
    {
        foreach (var place in Dialed.Domain.Geography.CountryCatalog.All)
        {
            CountryShapes.ByIso2.ContainsKey(place.Iso2).ShouldBeTrue(place.Name);
        }
    }

    [Fact]
    public void Markers_can_link_to_the_beans_of_that_country()
    {
        var map = Render<OriginMap>(p => p.Add(m => m.Origins, [("Ethiopia", 3)]).Add(m => m.LinkToBeans, true));

        var link = map.Find("a.dialed-origin-map-link");
        link.GetAttribute("href").ShouldBe("beans?country=ET");
        link.GetAttribute("aria-label").ShouldBe("Ethiopia: show beans");
    }
}
