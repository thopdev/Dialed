using System.Net;
using Bunit;
using Dialed.Client.Account;
using Dialed.UI.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

/// <summary>Passkey management, recovery codes and recovering a lost account (#112).</summary>
public sealed class PasskeysUiTests : UiTestContext
{
    private AuthSession Session => Services.GetRequiredService<AuthSession>();

    private AccountService Account => Services.GetRequiredService<AccountService>();

    private async Task<IRenderedComponent<PasskeysPanel>> SignedInPanel()
    {
        Tokens.Value = "tkn";
        await Session.SetAsync(new SessionTokens("a", DateTimeOffset.MaxValue, "r", "u1", "Thomas", "h-u1"), Xunit.TestContext.Current.CancellationToken);
        var panel = Render<PasskeysPanel>();
        panel.WaitForAssertion(() => panel.FindAll("#passkeys-list").Count.ShouldBe(1));
        return panel;
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private const string TwoPasskeys = "[{\"id\":\"p1\",\"name\":\"First passkey\",\"createdAt\":\"2026-01-01T00:00:00+00:00\",\"lastUsedAt\":null,\"backedUp\":false},{\"id\":\"p2\",\"name\":\"Laptop\",\"createdAt\":\"2026-02-01T00:00:00+00:00\",\"lastUsedAt\":null,\"backedUp\":false}]";

    [Fact]
    public async Task A_sign_in_with_no_account_behind_it_shows_no_error_and_no_passkey_controls()
    {
        DataServer.Respond = path => path.StartsWith("account/") ? new HttpResponseMessage(HttpStatusCode.NotFound) : null;
        Tokens.Value = "tkn";
        await Session.SetAsync(new SessionTokens("a", DateTimeOffset.MaxValue, "r", "u1", "Thomas", "h-u1"), Xunit.TestContext.Current.CancellationToken);

        var panel = Render<PasskeysPanel>();
        await Task.Delay(300, Xunit.TestContext.Current.CancellationToken);

        panel.FindAll("#passkeys-message").ShouldBeEmpty();
        panel.FindAll("#passkeys-add, #recovery-regenerate").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_still_says_so()
    {
        DataServer.Respond = path => path.StartsWith("account/") ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"detail\":\"No access.\"}", System.Text.Encoding.UTF8, "application/json") } : null;
        Tokens.Value = "tkn";
        await Session.SetAsync(new SessionTokens("a", DateTimeOffset.MaxValue, "r", "u1", "Thomas", "h-u1"), Xunit.TestContext.Current.CancellationToken);

        var panel = Render<PasskeysPanel>();

        panel.WaitForAssertion(() => panel.Find("#passkeys-message").TextContent.ShouldNotBeNullOrWhiteSpace());
    }

    [Fact]
    public void Nothing_shows_while_signed_out()
    {
        Render<PasskeysPanel>().FindAll("#passkeys-panel").ShouldBeEmpty();
    }

    [Fact]
    public async Task Lists_the_passkeys_and_nudges_towards_a_second_one()
    {
        var panel = await SignedInPanel();

        panel.FindAll(".passkey-row").Count.ShouldBe(1);
        panel.Find(".passkey-name").TextContent.ShouldBe("First passkey");
        panel.FindAll("#passkeys-second-hint").Count.ShouldBe(1);
        panel.FindAll(".passkey-remove").ShouldBeEmpty(); // the only passkey cannot be removed
        panel.Find("#recovery-left").TextContent.ShouldContain("8");
    }

    [Fact]
    public async Task No_nudge_once_there_are_two_passkeys()
    {
        DataServer.PasskeyList = TwoPasskeys;
        DataServer.RecoveryJson = "{\"recoveryCodesLeft\":8,\"hasBackedUpPasskey\":false,\"passkeys\":2}";

        var panel = await SignedInPanel();

        panel.FindAll(".passkey-row").Count.ShouldBe(2);
        panel.FindAll("#passkeys-second-hint").ShouldBeEmpty();
    }

    [Fact]
    public async Task Adding_a_passkey_runs_the_ceremony_and_reloads_the_list()
    {
        var panel = await SignedInPanel();

        panel.Find("#passkeys-add").Click();

        panel.WaitForAssertion(() => DataServer.Paths.Count(p => p == "account/passkeys").ShouldBeGreaterThanOrEqualTo(3)); // list, add, list again
        DataServer.Paths.ShouldContain("account/passkeys/options");
    }

    [Fact]
    public async Task A_dismissed_prompt_adds_nothing()
    {
        Passkeys.Next = new Dialed.Client.Platform.PasskeyOutcome(Dialed.Client.Platform.PasskeyStatus.Cancelled);
        var panel = await SignedInPanel();

        panel.Find("#passkeys-add").Click();

        panel.WaitForAssertion(() => panel.Find("#passkeys-message").TextContent.ShouldNotBeEmpty());
        DataServer.Paths.Where(p => p == "account/passkeys").Count().ShouldBe(1); // only the initial list
    }

    [Fact]
    public async Task Renaming_sends_the_new_name()
    {
        DataServer.PasskeyList = TwoPasskeys;
        var panel = await SignedInPanel();

        panel.FindAll(".passkey-rename")[1].Click();
        panel.Find(".passkey-name-input").Input("Work laptop");
        panel.Find(".passkey-save").Click();

        panel.WaitForAssertion(() => DataServer.Paths.ShouldContain("PUT account/passkeys/p2"));
    }

    [Fact]
    public async Task Removing_asks_first_and_then_deletes()
    {
        DataServer.PasskeyList = TwoPasskeys;
        var panel = await SignedInPanel();

        panel.FindAll(".passkey-remove")[1].Click();
        DataServer.Paths.ShouldNotContain("DELETE account/passkeys/p2");
        panel.Find(".passkey-remove-confirm").Click();

        panel.WaitForAssertion(() => DataServer.Paths.ShouldContain("DELETE account/passkeys/p2"));
    }

    [Fact]
    public async Task The_server_refusing_to_remove_the_last_passkey_is_shown()
    {
        DataServer.PasskeyList = TwoPasskeys;
        DataServer.Respond = path => path == "DELETE account/passkeys/p2" ? new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{\"detail\":\"You cannot remove your last passkey.\"}", System.Text.Encoding.UTF8, "application/json") } : null;
        var panel = await SignedInPanel();

        panel.FindAll(".passkey-remove")[1].Click();
        panel.Find(".passkey-remove-confirm").Click();

        panel.WaitForAssertion(() => panel.Find("#passkeys-message").TextContent.ShouldContain("last passkey"));
    }

    [Fact]
    public async Task New_recovery_codes_are_shown_once_until_dismissed()
    {
        var panel = await SignedInPanel();

        panel.Find("#recovery-regenerate").Click();

        panel.WaitForAssertion(() => panel.FindAll("#recovery-codes li").Count.ShouldBe(2));
        panel.Markup.ShouldContain("NEW1-AAAA");
        panel.Find("#recovery-codes-saved").Click();
        panel.WaitForAssertion(() => panel.FindAll("#recovery-codes").ShouldBeEmpty());
        Account.PendingRecoveryCodes.ShouldBeNull();
    }

    [Fact]
    public void Signing_up_keeps_the_codes_out_of_the_stored_session_and_shows_them()
    {
        Accounts.Respond = path => path == "auth/signup"
            ? Json("{\"accessToken\":\"a\",\"accessTokenExpiresAt\":\"2099-01-01T00:00:00+00:00\",\"refreshToken\":\"r\",\"userId\":\"u1\",\"displayName\":\"Thomas\",\"householdId\":\"h-u1\",\"recoveryCodes\":[\"AAAA-1111\",\"BBBB-2222\"]}")
            : null;
        Tokens.Value = "tkn";
        var panel = Render<PasskeysPanel>();
        var account = Render<AccountPanel>();

        account.Find("#account-name").Input("Thomas");
        account.Find("#account-email").Input("thomas@example.com");
        account.Find("#account-signup-form").Submit();

        panel.WaitForAssertion(() => panel.FindAll("#recovery-codes li").Count.ShouldBe(2));
        Session.Tokens!.RecoveryCodes.ShouldBeNull();
    }

    [Fact]
    public void Lost_passkey_recovery_signs_in_with_a_code()
    {
        var panel = Render<AccountPanel>();

        panel.Find("#account-lost").Click();
        panel.Find("#recover-email").Input("thomas@example.com");
        panel.Find("#recover-code").Input("ABCD-2345");
        panel.Find("#account-recover-form").Submit();

        panel.WaitForAssertion(() => Session.IsSignedIn.ShouldBeTrue());
        Accounts.Paths.ShouldContain("auth/recover/options");
        Accounts.Paths.ShouldContain("auth/recover");
        Accounts.Bodies[Accounts.Paths.IndexOf("auth/recover/options")].ShouldContain("ABCD-2345");
    }

    [Fact]
    public void Recovery_can_ask_for_an_email_code_and_says_nothing_about_the_account()
    {
        var panel = Render<AccountPanel>();

        panel.Find("#account-lost").Click();
        panel.Find("#recover-email").Input("thomas@example.com");
        panel.Find("#recover-send-email").Click();

        panel.WaitForAssertion(() => panel.Find("#account-message").TextContent.ShouldContain("If that address"));
        Accounts.Paths.ShouldContain("auth/recover/email");
    }

    [Fact]
    public void A_wrong_code_shows_the_refusal_and_stays_signed_out()
    {
        Accounts.Respond = path => path == "auth/recover/options"
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"detail\":\"That code is not valid.\"}", System.Text.Encoding.UTF8, "application/json") }
            : null;
        var panel = Render<AccountPanel>();

        panel.Find("#account-lost").Click();
        panel.Find("#recover-email").Input("thomas@example.com");
        panel.Find("#recover-code").Input("WRONG");
        panel.Find("#account-recover-form").Submit();

        panel.WaitForAssertion(() => panel.Find("#account-message").TextContent.ShouldContain("not valid"));
        Session.IsSignedIn.ShouldBeFalse();
    }
}
