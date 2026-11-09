using System.Net;
using System.Text;
using Bunit;
using Dialed.Client.Account;
using Dialed.Client.Data;
using Dialed.Client.Platform;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class AccountUiTests : UiTestContext
{
    private AuthSession Session => Services.GetRequiredService<AuthSession>();

    private IRenderedComponent<AccountPanel> Panel() => Render<AccountPanel>();

    private static void FillAndSignUp(IRenderedComponent<AccountPanel> panel)
    {
        panel.Find("#account-name").Input("Thomas");
        panel.Find("#account-email").Input("thomas@example.com");
        panel.Find("#account-signup-form").Submit();
    }

    [Fact]
    public void Signed_out_offers_sign_up_and_sign_in_and_says_data_stays_local()
    {
        var panel = Panel();

        panel.FindAll("#account-signup").Count.ShouldBe(1);
        panel.FindAll("#account-signin").Count.ShouldBe(1);
        panel.Markup.ShouldContain("stays on this device");
        panel.FindAll("#account-signout").ShouldBeEmpty();
    }

    [Fact]
    public void Signing_up_signs_in_and_shows_the_name()
    {
        var panel = Panel();

        FillAndSignUp(panel);

        panel.WaitForAssertion(() => panel.Find("#account-signed-in").TextContent.ShouldContain("Thomas"));
        Session.IsSignedIn.ShouldBeTrue();
        Accounts.Paths.ShouldContain("auth/signup");
        panel.FindAll("#account-signup").ShouldBeEmpty();
    }

    [Fact]
    public void The_invite_code_is_sent_with_the_sign_up()
    {
        var panel = Panel();
        panel.Find("#account-invite").Input(" friends2026 ");

        FillAndSignUp(panel);

        panel.WaitForAssertion(() => Accounts.Bodies[Accounts.Paths.IndexOf("auth/signup/options")].ShouldContain("\"inviteCode\":\"friends2026\""));
    }

    [Fact]
    public void An_invalid_email_is_caught_before_asking_the_server()
    {
        var panel = Panel();
        panel.Find("#account-email").Input("nope");
        panel.Find("#account-signup-form").Submit();

        panel.Find("#account-message").TextContent.ShouldContain("valid email");
        Accounts.Paths.ShouldBeEmpty();
    }

    [Fact]
    public void A_dismissed_passkey_prompt_changes_nothing()
    {
        Passkeys.Next = new PasskeyOutcome(PasskeyStatus.Cancelled);
        var panel = Panel();

        FillAndSignUp(panel);

        panel.WaitForAssertion(() => panel.Find("#account-message").TextContent.ShouldContain("Cancelled"));
        Session.IsSignedIn.ShouldBeFalse();
    }

    [Fact]
    public void A_browser_without_passkeys_says_so()
    {
        Passkeys.Supported = false;
        var panel = Panel();

        panel.Find("#account-signin").Click();

        panel.WaitForAssertion(() => panel.Find("#account-message").TextContent.ShouldContain("cannot use passkeys"));
    }

    [Fact]
    public void The_servers_reason_is_shown_when_it_refuses()
    {
        Accounts.Respond = path => path == "auth/signup/options"
            ? new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent("""{"detail":"This email address already has an account."}""", Encoding.UTF8, "application/json"),
            }
            : null;
        var panel = Panel();

        FillAndSignUp(panel);

        panel.WaitForAssertion(() => panel.Find("#account-message").TextContent.ShouldContain("already has an account"));
    }

    [Fact]
    public void An_unreachable_server_asks_to_try_again()
    {
        Accounts.Respond = _ => throw new HttpRequestException("offline");
        var panel = Panel();

        panel.Find("#account-signin").Click();

        panel.WaitForAssertion(() => panel.Find("#account-message").TextContent.ShouldContain("try again"));
        Session.IsSignedIn.ShouldBeFalse();
    }

    [Fact]
    public async Task Signing_out_returns_to_the_signed_out_view_and_tells_the_server()
    {
        await Session.SetAsync(new SessionTokens("a", DateTimeOffset.MaxValue, "r", "u1", "Thomas", "h-u1"), Xunit.TestContext.Current.CancellationToken);
        var panel = Panel();

        panel.Find("#account-signout").Click();

        panel.WaitForAssertion(() => panel.FindAll("#account-signup").Count.ShouldBe(1));
        Session.IsSignedIn.ShouldBeFalse();
        Accounts.Paths.ShouldContain("auth/signout");
    }
}
