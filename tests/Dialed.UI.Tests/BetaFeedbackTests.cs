using Bunit;
using Dialed.UI.Components;
using Dialed.UI.Pages;

namespace Dialed.UI.Tests;

public sealed class BetaFeedbackTests : UiTestContext
{
    [Fact]
    public void Without_a_feedback_address_the_panel_says_so_and_still_offers_known_issues()
    {
        var panel = Render<FeedbackPanel>();

        panel.FindAll("#feedback-link").ShouldBeEmpty();
        panel.Find("#feedback-unavailable").ShouldNotBeNull();
        panel.Find("#known-issues-link").GetAttribute("href").ShouldBe("known-issues");
    }

    [Fact]
    public void The_feedback_mail_holds_the_app_version_and_the_device()
    {
        Environment.FeedbackEmail = "feedback@example.com";

        var href = Render<FeedbackPanel>().Find("#feedback-link").GetAttribute("href")!;

        href.ShouldStartWith("mailto:feedback@example.com?subject=");
        var body = Uri.UnescapeDataString(href[(href.IndexOf("body=", StringComparison.Ordinal) + 5)..]);
        body.ShouldContain("Dialed ");
        body.ShouldContain("Device: phone, iOS 18.1, installed app");
        body.ShouldContain("What happened:");
    }

    [Fact]
    public void Settings_has_the_feedback_section_and_the_known_issues_list_has_items()
    {
        Render<Settings>().Find("#settings-feedback #known-issues-link").ShouldNotBeNull();

        Render<KnownIssues>().FindAll("#known-issues li").Count.ShouldBeGreaterThanOrEqualTo(3);
    }
}
