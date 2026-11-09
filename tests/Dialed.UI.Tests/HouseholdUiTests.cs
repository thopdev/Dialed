using System.Net;
using System.Text;
using Bunit;
using Dialed.Client.Account;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class HouseholdUiTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private async Task SignIn()
    {
        Tokens.Value = "tkn";
        await Services.GetRequiredService<AuthSession>().SetAsync(new SessionTokens("a", DateTimeOffset.MaxValue, "r", "u1", "You", "h1"), Ct);
    }

    private IRenderedComponent<HouseholdPanel> Panel() => Render<HouseholdPanel>();

    [Fact]
    public void Signed_out_points_to_the_account_section()
    {
        var panel = Panel();

        panel.Find("#household-signin-hint").TextContent.ShouldContain("Sign in");
        HouseholdServer.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Shows_the_household_and_its_members_with_the_owner_marked()
    {
        await SignIn();
        HouseholdServer.Members = [("You", "u1"), ("Bob", "bob")];

        var panel = Panel();

        panel.WaitForAssertion(() => panel.Find("#household-name").TextContent.ShouldBe("Our household"));
        panel.FindAll(".household-member").Count.ShouldBe(2);
        panel.Find(".household-member[data-user=u1]").TextContent.ShouldContain("owner");
    }

    [Fact]
    public async Task The_owner_creates_an_invite_code()
    {
        await SignIn();
        var panel = Panel();
        panel.WaitForElement("#household-invite");

        panel.Find("#household-invite").Click();

        panel.WaitForAssertion(() => panel.Find("#household-invite-code").TextContent.ShouldContain("ABCD2345"));
    }

    [Fact]
    public async Task Someone_alone_can_join_with_a_code_and_the_session_is_renewed()
    {
        await SignIn();
        var panel = Panel();
        panel.WaitForElement("#household-join-form");

        panel.Find("#household-code-input").Input("abcd2345");
        panel.Find("#household-join-form").Submit();

        panel.WaitForAssertion(() => panel.FindAll(".household-member").Count.ShouldBe(2));
        HouseholdServer.Calls.ShouldContain("POST households/mine/join");
        Accounts.Paths.ShouldContain("auth/refresh"); // the token carries the household
        panel.FindAll("#household-join-form").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_bad_code_shows_the_servers_reason()
    {
        await SignIn();
        HouseholdServer.Respond = call => call == "POST households/mine/join"
            ? new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"detail\":\"That invite code is not valid.\"}", Encoding.UTF8, "application/json"),
            }
            : null;
        var panel = Panel();
        panel.WaitForElement("#household-join-form");

        panel.Find("#household-code-input").Input("WRONG");
        panel.Find("#household-join-form").Submit();

        panel.WaitForAssertion(() => panel.Find("#household-message").TextContent.ShouldContain("not valid"));
    }

    [Fact]
    public async Task A_member_leaves_after_confirming()
    {
        await SignIn();
        HouseholdServer.Members = [("Owner", "owner"), ("You", "u1")];
        HouseholdServer.YouAreOwner = false;
        var panel = Panel();
        panel.WaitForElement("#household-leave");

        panel.Find("#household-leave").Click();
        HouseholdServer.Calls.ShouldNotContain("POST households/mine/leave"); // asks first
        panel.Find("#household-leave-confirm").Click();

        panel.WaitForAssertion(() => panel.FindAll(".household-member").Count.ShouldBe(1));
        HouseholdServer.Calls.ShouldContain("POST households/mine/leave");
    }

    [Fact]
    public async Task The_owner_removes_a_member_after_confirming()
    {
        await SignIn();
        HouseholdServer.Members = [("You", "u1"), ("Bob", "bob")];
        var panel = Panel();
        panel.WaitForElement(".household-remove");

        panel.Find(".household-remove").Click();
        HouseholdServer.Calls.ShouldNotContain("DELETE households/mine/members/bob");
        panel.Find(".household-remove-confirm").Click();

        panel.WaitForAssertion(() => panel.FindAll(".household-member").Count.ShouldBe(1));
        HouseholdServer.Calls.ShouldContain("DELETE households/mine/members/bob");
    }

    [Fact]
    public async Task The_owner_hands_over_ownership_after_confirming()
    {
        await SignIn();
        HouseholdServer.Members = [("You", "u1"), ("Bob", "bob")];
        var panel = Panel();
        panel.WaitForElement(".household-transfer");

        panel.Find(".household-transfer").Click();
        HouseholdServer.Calls.ShouldNotContain("POST households/mine/transfer");
        panel.Find(".household-transfer-confirm").Click();

        panel.WaitForAssertion(() => panel.FindAll(".household-transfer").ShouldBeEmpty());
        HouseholdServer.Calls.ShouldContain("POST households/mine/transfer");
        panel.FindAll("#household-invite").ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unreachable_server_offers_a_retry()
    {
        await SignIn();
        HouseholdServer.Respond = _ => throw new HttpRequestException("offline");
        var panel = Panel();

        panel.WaitForElement("#household-retry");
    }
}
