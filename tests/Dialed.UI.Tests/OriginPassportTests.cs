using Bunit;
using Dialed.UI.Components;

namespace Dialed.UI.Tests;

public sealed class OriginPassportTests : UiTestContext
{
    [Fact]
    public void Lights_up_the_coffee_countries_you_brewed_and_counts_them()
    {
        var page = Render<OriginPassport>(p => p.Add(c => c.Countries, ["Ethiopia", "ethiopië", "Kenya", "Atlantis", "Netherlands"]));

        page.FindAll(".dialed-passport-tried").Select(li => li.TextContent.Trim()).ShouldBe(["🇪🇹 Ethiopia", "🇰🇪 Kenya"]);
        page.Find("#passport-progress").TextContent.ShouldStartWith("2 of ");
        page.Find(".dialed-passport-more summary").TextContent.ShouldContain("Still to try");
        page.FindAll(".dialed-passport-more li").Select(li => li.TextContent).ShouldNotContain(t => t.Contains("Ethiopia"));
    }

    [Fact]
    public void Invites_you_to_start_when_nothing_is_brewed_yet()
    {
        var page = Render<OriginPassport>(p => p.Add(c => c.Countries, []));

        page.Find("#passport-empty").TextContent.ShouldContain("Brew a coffee");
        page.FindAll(".dialed-passport-tried").ShouldBeEmpty();
    }
}
