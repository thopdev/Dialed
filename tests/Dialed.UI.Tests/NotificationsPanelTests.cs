using Bunit;
using Dialed.Client.Account;
using Dialed.Client.Platform;
using Dialed.UI.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

/// <summary>Settings > Notifications in every state (#100).</summary>
public sealed class NotificationsPanelTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private async Task SignIn() =>
        await Services.GetRequiredService<AuthSession>().SetAsync(new SessionTokens("a", DateTimeOffset.MaxValue, "r", "u1", "Thomas", "h-u1"), Ct);

    private IRenderedComponent<NotificationsPanel> Panel() => Render<NotificationsPanel>();

    [Fact]
    public void Signed_out_people_are_asked_to_sign_in()
    {
        var panel = Panel();

        panel.Find("#push-status").TextContent.ShouldContain("Sign in");
        panel.FindAll("#push-enable").ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unsupported_browser_says_so()
    {
        await SignIn();
        PushDevice.Support = PushSupport.Unsupported;

        Panel().Find("#push-status").TextContent.ShouldContain("cannot receive notifications");
    }

    [Fact]
    public async Task An_iPhone_in_Safari_is_told_to_add_the_app_to_the_Home_Screen_first()
    {
        await SignIn();
        PushDevice.Support = PushSupport.NotInstalled;

        var panel = Panel();

        panel.Find("#push-status").TextContent.ShouldContain("Home Screen");
        panel.FindAll("#push-enable").ShouldBeEmpty();
    }

    [Fact]
    public async Task When_not_asked_yet_the_button_explains_and_allows()
    {
        await SignIn();

        var panel = Panel();

        panel.Find("#push-status").TextContent.ShouldContain("off on this device");
        panel.Find("#push-enable").TextContent.ShouldBe("Allow notifications");
        panel.Find(".dialed-muted").TextContent.ShouldContain("only says who or which bean");
    }

    [Fact]
    public async Task Allowing_asks_the_browser_and_registers_this_device_with_the_app_language()
    {
        await SignIn();
        var panel = Panel();

        panel.Find("#push-enable").Click();

        panel.WaitForAssertion(() => panel.Find("#push-status").TextContent.ShouldContain("on for this device"));
        PushDevice.PublicKeyUsed.ShouldBe("public-vapid-key");
        var registered = PushServer.Registered.ShouldHaveSingleItem();
        registered.Device.Endpoint.ShouldBe("https://push.test/device-1");
        registered.Language.ShouldBe("en");
        panel.Find("#push-follows").HasAttribute("checked").ShouldBeTrue();
        panel.Find("#push-household").HasAttribute("checked").ShouldBeTrue();
    }

    [Fact]
    public async Task Saying_no_in_the_browser_shows_how_to_allow_it_again()
    {
        await SignIn();
        PushDevice.Answer = PushPermission.Denied;
        var panel = Panel();

        panel.Find("#push-enable").Click();

        panel.WaitForAssertion(() => panel.Find("#push-status").TextContent.ShouldContain("blocked"));
        PushServer.Registered.ShouldBeEmpty();
        panel.FindAll("#push-enable").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_permission_that_was_denied_earlier_shows_blocked_straight_away()
    {
        await SignIn();
        PushDevice.Permission = PushPermission.Denied;

        Panel().Find("#push-status").TextContent.ShouldContain("blocked");
    }

    [Fact]
    public async Task A_server_without_push_says_it_is_not_available_and_can_be_retried()
    {
        await SignIn();
        PushServer.Key = null;
        var panel = Panel();

        panel.Find("#push-enable").Click();

        panel.WaitForAssertion(() => panel.Find("#push-status").TextContent.ShouldContain("not available right now"));
        panel.Find("#push-retry").ShouldNotBeNull();
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_never_breaks_the_screen()
    {
        await SignIn();
        PushServer.Down = true;

        Panel().Find("#push-status").TextContent.ShouldContain("not available right now");
    }

    [Fact]
    public async Task The_switches_apply_at_once()
    {
        await SignIn();
        var panel = Panel();
        panel.Find("#push-enable").Click();
        panel.WaitForElement("#push-follows");

        panel.Find("#push-follows").Change(false);

        panel.WaitForAssertion(() => PushServer.Follows.ShouldBeFalse());
        PushServer.Household.ShouldBeTrue();
        panel.Find("#push-household").Change(false);
        panel.WaitForAssertion(() => PushServer.Household.ShouldBeFalse());
    }

    [Fact]
    public async Task Turning_it_off_removes_this_device_here_and_at_the_server()
    {
        await SignIn();
        var panel = Panel();
        panel.Find("#push-enable").Click();
        panel.WaitForElement("#push-disable");

        panel.Find("#push-disable").Click();

        panel.WaitForAssertion(() => panel.Find("#push-status").TextContent.ShouldContain("off on this device"));
        PushServer.Removed.ShouldBe(["https://push.test/device-1"]);
        PushDevice.Subscription.ShouldBeNull();
    }

    [Fact]
    public async Task Signing_out_removes_this_device_before_the_token_is_gone()
    {
        await SignIn();
        Services.GetRequiredService<Dialed.Client.Push.PushNotificationService>();
        await Services.GetRequiredService<Dialed.Client.Push.PushNotificationService>().EnableAsync(Ct);

        await Services.GetRequiredService<AccountService>().SignOutAsync(Ct);

        PushServer.Removed.ShouldBe(["https://push.test/device-1"]);
        PushDevice.Subscription.ShouldBeNull();
    }

    [Fact]
    public async Task Signing_out_works_when_the_server_is_unreachable()
    {
        await SignIn();
        await Services.GetRequiredService<Dialed.Client.Push.PushNotificationService>().EnableAsync(Ct);
        PushServer.Down = true;

        await Services.GetRequiredService<AccountService>().SignOutAsync(Ct);

        Services.GetRequiredService<AuthSession>().IsSignedIn.ShouldBeFalse();
        PushDevice.Subscription.ShouldBeNull();
    }
}
