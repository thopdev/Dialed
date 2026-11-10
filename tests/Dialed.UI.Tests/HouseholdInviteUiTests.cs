using Bunit;
using Dialed.Client.Account;
using Dialed.UI.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

/// <summary>Household invites: how many may join, share, the open list and revoking (#71).</summary>
public sealed class HouseholdInviteUiTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private async Task<IRenderedComponent<HouseholdPanel>> OwnerPanel()
    {
        Tokens.Value = "tkn";
        await Services.GetRequiredService<AuthSession>().SetAsync(new SessionTokens("a", DateTimeOffset.MaxValue, "r", "u1", "You", "h1"), Ct);
        var panel = Render<HouseholdPanel>();
        panel.WaitForElement("#household-invite");
        return panel;
    }

    [Fact]
    public async Task The_owner_chooses_how_many_people_the_code_is_for()
    {
        var panel = await OwnerPanel();

        panel.FindAll("#household-invite-uses option").Select(o => o.TextContent).ShouldBe(["1 person", "2 people", "3 people", "5 people", "10 people"]);
        panel.Find("#household-invite-uses").Change("5");
        panel.Find("#household-invite").Click();

        panel.WaitForAssertion(() => panel.Find("#household-open-invites .household-open-invite").TextContent.ShouldContain("0 of 5 joined"));
        var text = panel.Find("#household-invite-code").TextContent;
        text.ShouldStartWith("Invite code: ABCD2345 (valid until ");
        text.ShouldEndWith(", for 5 people)");
        text.ShouldNotContain("works once");
        HouseholdServer.Invites.Single().MaxUses.ShouldBe(5);
    }

    [Fact]
    public async Task Open_invites_are_listed_with_progress_and_expiry()
    {
        HouseholdServer.Invites.Add(("AAAA2222", 3, 1));
        HouseholdServer.Invites.Add(("BBBB3333", 1, 0));

        var panel = await OwnerPanel();

        panel.WaitForAssertion(() => panel.FindAll(".household-open-invite").Count.ShouldBe(2));
        var first = panel.Find(".household-open-invite[data-code=AAAA2222]");
        first.TextContent.ShouldContain("1 of 3 joined");
        first.TextContent.ShouldContain("expires 1 Jan");
        panel.Find("#household-open-invites-title").TextContent.ShouldBe("Open invites");
    }

    [Fact]
    public async Task Revoking_removes_the_invite_and_says_so_and_the_button_names_the_code()
    {
        HouseholdServer.Invites.Add(("AAAA2222", 1, 0));
        HouseholdServer.Invites.Add(("BBBB3333", 1, 0));
        var panel = await OwnerPanel();
        panel.WaitForAssertion(() => panel.FindAll(".household-open-invite").Count.ShouldBe(2));

        var revoke = panel.Find(".household-open-invite[data-code=AAAA2222] .household-invite-revoke");
        revoke.GetAttribute("aria-label").ShouldBe("Revoke invite AAAA2222");
        revoke.Click();

        panel.WaitForAssertion(() => panel.FindAll(".household-open-invite").Select(i => i.GetAttribute("data-code")).ShouldBe(["BBBB3333"]));
        panel.Find("#household-message").TextContent.ShouldBe("Invite AAAA2222 revoked.");
        HouseholdServer.Invites.Select(i => i.Code).ShouldBe(["BBBB3333"]);
    }

    [Fact]
    public async Task A_new_invite_can_be_shared_when_the_device_has_a_share_sheet()
    {
        var panel = await OwnerPanel();
        panel.Find("#household-invite").Click();
        panel.WaitForElement("#household-invite-share");

        panel.Find("#household-invite-share").Click();

        var shared = Shares.Shared.Single();
        shared.Title.ShouldBe("Join my Dialed household");
        shared.Text.ShouldBe("Join my household on Dialed: open Settings, then Household, and enter the code ABCD2345.");
        shared.Url.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Without_a_share_sheet_the_code_is_still_shown_and_there_is_no_share_button()
    {
        Shares.Supported = false;
        var panel = await OwnerPanel();

        panel.Find("#household-invite").Click();

        panel.WaitForAssertion(() => panel.Find("#household-invite-code").TextContent.ShouldContain("ABCD2345"));
        panel.FindAll("#household-invite-share").ShouldBeEmpty();
    }

    [Fact]
    public async Task Members_who_are_not_the_owner_see_no_invite_controls_or_list()
    {
        HouseholdServer.YouAreOwner = false;
        HouseholdServer.Members = [("Owner", "owner"), ("You", "u1")];
        HouseholdServer.Invites.Add(("AAAA2222", 1, 0));
        Tokens.Value = "tkn";
        await Services.GetRequiredService<AuthSession>().SetAsync(new SessionTokens("a", DateTimeOffset.MaxValue, "r", "u1", "You", "h1"), Ct);

        var panel = Render<HouseholdPanel>();
        panel.WaitForElement("#household-name");

        panel.FindAll("#household-invite, #household-invite-uses, #household-open-invites").ShouldBeEmpty();
        HouseholdServer.Calls.ShouldNotContain("GET households/mine/invites");
    }
}
