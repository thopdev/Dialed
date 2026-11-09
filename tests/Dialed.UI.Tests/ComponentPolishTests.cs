using Bunit;
using Dialed.UI.Components;

namespace Dialed.UI.Tests;

public sealed class ComponentPolishTests : UiTestContext
{
    [Theory]
    [InlineData("Thomas Hopp", "TH")]
    [InlineData("  iris ", "I")]
    [InlineData("Otto de Hond", "OH")]
    [InlineData("", "?")]
    [InlineData(null, "?")]
    public void Initials_come_from_the_first_and_last_word(string? name, string expected) => Avatar.InitialsOf(name).ShouldBe(expected);

    [Fact]
    public void The_same_name_always_gets_the_same_colour_and_it_is_one_of_the_palette()
    {
        Avatar.HueOf("Thomas").ShouldBe(Avatar.HueOf("Thomas"));
        foreach (var name in new[] { "a", "Iris", "Otto", "Thomas Hopp", "" })
        {
            Avatar.HueOf(name).ShouldBeInRange(0, 5);
        }
    }

    [Fact]
    public void An_avatar_is_announced_by_name_not_by_its_letters()
    {
        var avatar = Render<Avatar>(p => p.Add(a => a.Name, "Thomas Hopp"));

        avatar.Find(".dialed-avatar").GetAttribute("aria-label").ShouldBe("Thomas Hopp");
        avatar.Find("[aria-hidden=true]").TextContent.ShouldBe("TH");
    }

    [Fact]
    public void A_stat_tile_shows_value_label_and_hint()
    {
        var tile = Render<StatTile>(p => p.Add(t => t.Value, "182 g").Add(t => t.Label, "Remaining").Add(t => t.Hint, "of 250 g"));

        tile.Find(".dialed-stat-value").TextContent.ShouldBe("182 g");
        tile.Find(".dialed-stat-label").TextContent.ShouldBe("Remaining");
        tile.Find(".dialed-stat-hint").TextContent.ShouldBe("of 250 g");
    }

    [Theory]
    [InlineData(0.5, 50)]
    [InlineData(2.0, 100)]
    [InlineData(-1.0, 0)]
    [InlineData(double.NaN, 0)]
    public void A_meter_clamps_and_is_announced_as_a_meter(double fraction, int percent)
    {
        var meter = Render<Meter>(p => p.Add(m => m.Label, "Remaining").Add(m => m.Fraction, fraction));

        var element = meter.Find("[role=meter]");
        element.GetAttribute("aria-valuenow").ShouldBe(percent.ToString(System.Globalization.CultureInfo.InvariantCulture));
        element.GetAttribute("aria-label").ShouldBe("Remaining");
        meter.Find(".dialed-meter-fill").GetAttribute("style")!.ShouldContain($"width: {percent}%");
    }

    [Fact]
    public void A_meter_can_speak_in_the_units_of_the_thing()
    {
        var meter = Render<Meter>(p => p.Add(m => m.Label, "Remaining").Add(m => m.Fraction, 0.73).Add(m => m.ValueText, "182 of 250 g"));

        meter.Find("[role=meter]").GetAttribute("aria-valuetext").ShouldBe("182 of 250 g");
    }

    [Fact]
    public void Without_a_photo_a_thumbnail_shows_the_placeholder_and_is_decorative()
    {
        var thumb = Render<Thumb>();

        thumb.FindAll("img").ShouldBeEmpty();
        thumb.Find(".dialed-thumb-placeholder").GetAttribute("aria-hidden").ShouldBe("true");
    }

    [Fact]
    public void A_section_header_has_a_heading_and_optional_actions()
    {
        var header = Render<SectionHeader>(p => p.Add(h => h.Title, "Recent brews"));

        header.Find("h2").TextContent.ShouldBe("Recent brews");
        header.FindAll(".dialed-section-actions").ShouldBeEmpty();
    }
}
