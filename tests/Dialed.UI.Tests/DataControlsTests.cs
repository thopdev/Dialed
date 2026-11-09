using System.Net;
using System.Text;
using Bunit;
using Bunit.TestDoubles;
using Dialed.Client.Account;
using Dialed.Client.Platform;
using Dialed.UI.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class DataControlsTests : UiTestContext
{
    private AuthSession Session => Services.GetRequiredService<AuthSession>();

    private async Task<IRenderedComponent<DataControls>> SignedInPanel()
    {
        Tokens.Value = "tkn";
        await Session.SetAsync(new SessionTokens("a", DateTimeOffset.MaxValue, "r", "u1", "Thomas", "h-u1"), Xunit.TestContext.Current.CancellationToken);
        return Render<DataControls>();
    }

    [Fact]
    public void Nothing_shows_while_signed_out()
    {
        Render<DataControls>().FindAll("#data-controls").ShouldBeEmpty();
    }

    [Fact]
    public async Task Export_hands_the_json_file_to_the_person()
    {
        var panel = await SignedInPanel();

        panel.Find("#data-export").Click();

        panel.WaitForAssertion(() => panel.Find("#data-message").TextContent.ShouldContain("exported"));
        var (name, type, content) = Files.Saved.Single();
        name.ShouldStartWith("dialed-export-");
        name.ShouldEndWith(".json");
        type.ShouldBe("application/json");
        Encoding.UTF8.GetString(content).ShouldContain("formatVersion");
    }

    [Fact]
    public async Task A_dismissed_share_sheet_says_nothing()
    {
        Files.Dismiss = true;
        var panel = await SignedInPanel();

        panel.Find("#data-export").Click();

        panel.WaitForAssertion(() => panel.FindAll("#data-export").Count.ShouldBe(1));
        panel.FindAll("#data-message").ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_asks_for_confirmation_first()
    {
        var panel = await SignedInPanel();

        panel.Find("#data-delete").Click();

        panel.Find(".dialed-confirm").TextContent.ShouldContain("cannot be undone");
        DataServer.Paths.ShouldNotContain("account/delete");
    }

    [Fact]
    public async Task Confirming_checks_the_passkey_deletes_the_account_signs_out_and_reloads_the_app()
    {
        var panel = await SignedInPanel();
        panel.Find("#data-delete").Click();

        panel.Find("#data-delete-confirm").Click();

        panel.WaitForAssertion(() => Session.IsSignedIn.ShouldBeFalse());
        DataServer.Paths.ShouldBe(["account/delete/options", "account/delete"]);
        var navigation = (FakeNavigationManager)Services.GetRequiredService<NavigationManager>();
        navigation.History.First().Options.ForceLoad.ShouldBeTrue();
    }

    [Fact]
    public async Task A_dismissed_passkey_prompt_keeps_the_account()
    {
        Passkeys.Next = new PasskeyOutcome(PasskeyStatus.Cancelled);
        var panel = await SignedInPanel();
        panel.Find("#data-delete").Click();

        panel.Find("#data-delete-confirm").Click();

        panel.WaitForAssertion(() => panel.Find("#data-message").TextContent.ShouldContain("Cancelled"));
        Session.IsSignedIn.ShouldBeTrue();
        DataServer.Paths.ShouldNotContain("account/delete");
    }

    [Fact]
    public async Task When_the_server_refuses_nothing_is_wiped_and_the_reason_is_shown()
    {
        DataServer.Respond = path => path == "account/delete"
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"detail":"That passkey does not belong to this account."}""", Encoding.UTF8, "application/json"),
            }
            : null;
        var panel = await SignedInPanel();
        panel.Find("#data-delete").Click();

        panel.Find("#data-delete-confirm").Click();

        panel.WaitForAssertion(() => panel.Find("#data-message").TextContent.ShouldContain("does not belong"));
        Session.IsSignedIn.ShouldBeTrue();
    }

    [Fact]
    public async Task A_browser_without_passkeys_cannot_delete()
    {
        Passkeys.Supported = false;
        var panel = await SignedInPanel();
        panel.Find("#data-delete").Click();

        panel.Find("#data-delete-confirm").Click();

        panel.WaitForAssertion(() => panel.Find("#data-message").TextContent.ShouldContain("cannot use passkeys"));
        Session.IsSignedIn.ShouldBeTrue();
    }
}
