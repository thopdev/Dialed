using Bunit;
using Dialed.UI.Components;
using Dialed.UI.Layout;
using Dialed.UI.Pages;
using Microsoft.AspNetCore.Components;

namespace Dialed.UI.Tests;

public sealed class PwaTests : UiTestContext
{
    [Fact]
    public void No_update_banner_until_an_update_is_available()
    {
        var banner = Render<UpdateBanner>();

        banner.FindAll(".dialed-update-banner").ShouldBeEmpty();
    }

    [Fact]
    public void Update_banner_appears_when_an_update_arrives_and_reload_applies_it()
    {
        var layout = Render<MainLayout>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddContent(0, "Hi"))));

        Pwa.PublishUpdate();
        layout.WaitForAssertion(() => layout.Find(".dialed-update-banner").TextContent.ShouldContain("new version"));
        layout.Find(".dialed-update-banner button").Click();

        Pwa.UpdatesApplied.ShouldBe(1);
    }

    [Fact]
    public void Install_guide_is_shown_only_in_iOS_Safari_tabs()
    {
        Render<InstallGuide>().FindAll(".dialed-install-guide").ShouldBeEmpty();

        Pwa.NeedsInstallGuide = true;
        var guide = Render<InstallGuide>();

        guide.FindAll(".dialed-install-guide li").Count.ShouldBe(3);
        guide.Markup.ShouldContain("Add to Home Screen");
    }

    [Fact]
    public void Settings_page_hosts_the_install_guide()
    {
        Pwa.NeedsInstallGuide = true;

        Render<Settings>(p => p.Add(x => x.Group, "help")).FindAll(".dialed-install-guide").Count.ShouldBe(1);
    }
}
