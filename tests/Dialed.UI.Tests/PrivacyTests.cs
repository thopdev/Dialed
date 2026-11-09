using Bunit;
using Dialed.UI.Components;
using Dialed.UI.Pages;

namespace Dialed.UI.Tests;

public sealed class PrivacyTests : UiTestContext
{
    [Fact]
    public void The_policy_covers_data_photos_passkeys_ai_deletion_and_export()
    {
        var page = Render<Privacy>();

        foreach (var section in new[] { "stored", "account", "photos", "sharing", "ai", "errors", "control", "contact" })
        {
            page.FindAll($"#privacy-{section} h2").Count.ShouldBe(1, section);
        }

        page.Find("#privacy-ai").TextContent.ShouldContain("Microsoft Foundry");
        page.Find("#privacy-account").TextContent.ShouldContain("passkeys");
        page.Find("#privacy-control").TextContent.ShouldContain("export");
        page.Find("#privacy-draft").TextContent.ShouldContain("Draft");
    }

    [Fact]
    public void Settings_and_sign_up_link_to_the_policy()
    {
        Render<Settings>().Find("#privacy-link").GetAttribute("href").ShouldBe("privacy");
        Render<AccountPanel>().Find("#account-privacy-link").GetAttribute("href").ShouldBe("privacy");
    }

    [Fact]
    public void Where_photos_are_first_requested_the_camera_use_is_explained()
    {
        var strip = Render<PhotoStrip>();

        strip.Find(".photo-privacy-hint").TextContent.ShouldContain("only used when you tap");
    }
}
