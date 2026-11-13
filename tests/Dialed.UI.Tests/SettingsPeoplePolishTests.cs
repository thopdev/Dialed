using System.Net;
using Bunit;
using Dialed.Client.Account;
using Dialed.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

/// <summary>Settings, More, People and Onboarding feel organised (#216): grouped settings, icon rows, people with avatars and clear states, friendly setup steps.</summary>
public sealed class SettingsPeoplePolishTests : UiTestContext
{
    private AuthSession Session => Services.GetRequiredService<AuthSession>();

    private async Task SignIn()
    {
        Tokens.Value = "tkn";
        await Session.SetAsync(new SessionTokens("a", DateTimeOffset.MaxValue, "r", "u1", "Thomas", "h-u1"), Xunit.TestContext.Current.CancellationToken);
    }

    private static HttpResponseMessage People(params (string Id, string Name)[] people) =>
        TestFriendsServer.Json("[" + string.Join(",", people.Select(p => $"{{\"userId\":\"{p.Id}\",\"displayName\":\"{p.Name}\"}}")) + "]");

    // ---- Settings -----------------------------------------------------------------------------

    [Fact]
    public void Settings_overview_lists_the_groups_as_rows_with_an_icon_and_the_account_first()
    {
        var page = Render<Settings>();

        var rows = page.FindAll("#settings-groups .dialed-row");
        rows.Select(r => r.GetAttribute("href")).ShouldBe(["settings/profile", "settings/preferences", "settings/household", "settings/data", "settings/privacy", "settings/help"]);
        rows.ShouldAllBe(r => r.QuerySelector(".dialed-more-icon[aria-hidden=true] svg") != null);
        page.Find("#settings-signin").GetAttribute("href").ShouldBe("signin");
        page.Find("#settings-version").TextContent.ShouldStartWith("Version");
        page.Markup.ShouldNotContain("Available once accounts are in");
    }

    [Fact]
    public async Task Settings_overview_shows_the_signed_in_person_and_links_to_the_account_group()
    {
        await SignIn();

        var page = Render<Settings>();

        page.FindAll("#settings-signin").ShouldBeEmpty();
        page.Find("#settings-account-card").GetAttribute("href").ShouldBe("settings/account");
        page.Find("#settings-account-card").TextContent.ShouldContain("Thomas");
    }

    [Fact]
    public void A_settings_group_opens_on_its_own_page_with_a_back_link()
    {
        var page = Render<Settings>(p => p.Add(x => x.Group, "preferences"));

        page.Find("h1").TextContent.ShouldBe("Preferences");
        page.Find("a.dialed-back").GetAttribute("href").ShouldBe("settings");
        page.Find("#group-preferences #settings-units").ShouldNotBeNull();
    }

    [Fact]
    public void An_unknown_settings_group_shows_the_overview()
    {
        Render<Settings>(p => p.Add(x => x.Group, "nope")).FindAll("#settings-groups").Count.ShouldBe(1);
    }

    // ---- More ---------------------------------------------------------------------------------

    [Fact]
    public void More_lists_every_destination_with_an_icon_in_groups()
    {
        var page = Render<More>();

        page.FindAll(".dialed-more-group h2").Select(h => h.TextContent).ShouldBe(["Your coffee", "Insights", "Friends", "App"]);
        var rows = page.FindAll(".dialed-more-group .dialed-row");
        rows.Select(r => r.GetAttribute("href")).ShouldBe(["bags", "inventory", "vendors", "equipment", "history", "statistics", "people", "feed", "import", "settings"]);
        rows.ShouldAllBe(r => r.QuerySelector(".dialed-more-icon[aria-hidden=true] svg") != null);
    }

    // ---- People -------------------------------------------------------------------------------

    [Fact]
    public async Task People_rows_have_an_avatar_and_a_state_for_each_relationship()
    {
        FriendsServer.Respond = call => call switch
        {
            "GET friends/requests" => People(("u2", "Iris")),
            "GET friends/following" => People(("u3", "Carol")),
            "GET friends/following/pending" => People(("u4", "Dave")),
            "GET friends/followers" => People(("u5", "Erin")),
            "GET friends/blocks" => People(("u6", "Mallory")),
            _ => null,
        };
        await SignIn();

        var page = Render<People>();

        page.WaitForAssertion(() => page.Find(".people-blocked-person"));
        string State(string css) => page.Find($"{css} .people-state").TextContent;
        State(".people-request").ShouldBe("Wants to follow you");
        State(".people-followed").ShouldBe("Following");
        State(".people-pending").ShouldBe("Waiting for an answer");
        State(".people-follower").ShouldBe("Follows you");
        State(".people-blocked-person").ShouldBe("Blocked");
        page.FindAll(".people-row").ShouldAllBe(r => r.QuerySelector(".dialed-avatar[role=img]") != null);
        page.Find(".people-request .dialed-avatar").GetAttribute("aria-label").ShouldBe("Iris");
    }

    [Fact]
    public async Task The_friend_code_can_be_shared_when_the_device_can_share()
    {
        await SignIn();
        var page = Render<People>();
        page.WaitForAssertion(() => page.Find("#people-share"));

        page.Find("#people-share").Click();

        Shares.Shared.Single().Text.ShouldBe("Follow me on Dialed with my friend code ABCD2345.");
    }

    [Fact]
    public async Task Without_a_share_sheet_only_a_new_code_is_offered()
    {
        Shares.Supported = false;
        await SignIn();

        var page = Render<People>();

        page.WaitForAssertion(() => page.Find("#people-my-code").TextContent.ShouldBe("ABCD2345"));
        page.FindAll("#people-share").ShouldBeEmpty();
    }

    [Fact]
    public void Signed_out_people_page_points_to_signing_in()
    {
        var page = Render<People>();

        page.Find(".dialed-empty .dialed-empty-icon").ShouldNotBeNull();
        page.Find("#people-sign-in").GetAttribute("href").ShouldBe("signin");
    }

    [Fact]
    public async Task An_unavailable_profile_explains_why_and_leads_back()
    {
        FriendsServer.Respond = call => call == "GET friends/u9/profile" ? TestFriendsServer.Json("{\"detail\":\"Not found.\"}", HttpStatusCode.NotFound) : null;
        await SignIn();

        var page = Render<FriendProfile>(p => p.Add(x => x.UserId, "u9"));

        page.WaitForAssertion(() => page.Find(".dialed-empty-title").TextContent.ShouldBe("Not visible yet"));
        page.Find("#friend-back-people").GetAttribute("href").ShouldBe("people");
    }

    // ---- Onboarding ---------------------------------------------------------------------------

    [Fact]
    public void Onboarding_shows_progress_and_a_friendly_step_heading()
    {
        var page = Render<Onboarding>();

        var meter = page.Find(".dialed-onboarding-progress [role=meter]");
        meter.GetAttribute("aria-valuetext").ShouldBe("Step 1 of 3");
        meter.GetAttribute("aria-valuenow").ShouldBe("33");
        page.Find("#onboarding-step-title").TextContent.ShouldBe("Choose your units");
        page.Find(".dialed-onboarding-icon svg").ShouldNotBeNull();
        page.FindAll("h2").Select(h => h.TextContent).ShouldNotContain("Units");

        page.Find("#onboarding-next").Click();

        page.Find("#onboarding-step-title").TextContent.ShouldBe("How do you brew?");
        page.Find(".dialed-onboarding-progress [role=meter]").GetAttribute("aria-valuenow").ShouldBe("67");
    }

    [Fact]
    public void The_gear_step_has_labelled_fields_side_by_side()
    {
        var page = Render<Onboarding>();
        page.Find("#onboarding-next").Click();
        page.Find("#onboarding-next").Click();

        page.Find("#onboarding-step-title").TextContent.ShouldBe("Your gear");
        foreach (var id in new[] { "ob-grinder-brand", "ob-grinder-model", "ob-brewer-brand", "ob-brewer-model" })
        {
            page.Find($"label[for={id}]").ShouldNotBeNull();
        }

        page.FindAll(".dialed-onboarding-gear .dialed-field-row").Count.ShouldBe(2);
    }
}
