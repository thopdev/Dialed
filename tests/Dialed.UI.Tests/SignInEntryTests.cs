using Bunit;
using Dialed.UI.Pages;

namespace Dialed.UI.Tests;

public sealed class SignInEntryTests : UiTestContext
{
    [Fact]
    public void The_sign_in_page_shows_the_passkey_forms()
    {
        var page = Render<SignIn>();

        page.Find("#account-signin").ShouldNotBeNull();
        page.Find("#account-signup").ShouldNotBeNull();
        page.Find("#account-lost").ShouldNotBeNull();
    }

    [Fact]
    public void Onboarding_links_to_the_sign_in_page_for_people_who_have_an_account()
    {
        Render<Onboarding>().Find("#onboarding-signin").GetAttribute("href").ShouldBe("signin");
    }
}
