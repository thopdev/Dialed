using Bunit;
using Dialed.Client.Account;
using Dialed.UI.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

/// <summary>Creating, renaming and deleting households in the household panel (#70).</summary>
public sealed class HouseholdManageUiTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private async Task SignIn()
    {
        Tokens.Value = "tkn";
        await Services.GetRequiredService<AuthSession>().SetAsync(new SessionTokens("a", DateTimeOffset.MaxValue, "r", "u1", "You", "h1"), Ct);
    }

    [Fact]
    public async Task The_owner_renames_the_household()
    {
        await SignIn();
        var panel = Render<HouseholdPanel>();
        panel.WaitForElement("#household-rename");
        panel.Find("#household-rename").GetAttribute("aria-label").ShouldBe("Rename Our household");

        panel.Find("#household-rename").Click();
        panel.Find("#household-rename-input").GetAttribute("value").ShouldBe("Our household");
        panel.Find("#household-rename-input").Input("The coffee corner");
        panel.Find("#household-rename-form").Submit();

        panel.WaitForAssertion(() => panel.Find("#household-name").TextContent.ShouldBe("The coffee corner"));
        HouseholdServer.Calls.ShouldContain("PUT households/mine/name");
        panel.Find("#household-message").TextContent.ShouldBe("Household renamed.");
    }

    [Fact]
    public async Task A_rename_can_be_cancelled_and_needs_a_name()
    {
        await SignIn();
        var panel = Render<HouseholdPanel>();
        panel.WaitForElement("#household-rename");

        panel.Find("#household-rename").Click();
        panel.Find("#household-rename-input").Input("  ");
        panel.Find("#household-rename-save").HasAttribute("disabled").ShouldBeTrue();
        panel.Find("#household-rename-cancel").Click();

        panel.Find("#household-name").TextContent.ShouldBe("Our household");
        HouseholdServer.Calls.ShouldNotContain("PUT households/mine/name");
    }

    [Fact]
    public async Task A_member_cannot_rename_or_delete()
    {
        await SignIn();
        HouseholdServer.Members = [("Owner", "owner"), ("You", "u1")];
        HouseholdServer.YouAreOwner = false;
        HouseholdServer.Households = [("h1", "Home"), ("h2", "Office")];
        var panel = Render<HouseholdPanel>();
        panel.WaitForElement("#household-create");

        panel.FindAll("#household-rename").ShouldBeEmpty();
        panel.FindAll("#household-delete").ShouldBeEmpty();
    }

    [Fact]
    public async Task Anyone_creates_a_household_and_moves_to_it()
    {
        await SignIn();
        var panel = Render<HouseholdPanel>();
        panel.WaitForElement("#household-create");

        panel.Find("#household-create").Click();
        panel.Find("#household-create-save").HasAttribute("disabled").ShouldBeTrue();
        panel.Find("#household-create-name").Input("Office");
        panel.Find("#household-create-form").Submit();

        panel.WaitForAssertion(() => panel.Find("#household-name").TextContent.ShouldBe("Office"));
        HouseholdServer.Calls.ShouldContain("POST households");
        Accounts.Paths.ShouldContain("auth/refresh"); // the token carries the new household
        panel.Find("#household-message").TextContent.ShouldContain("Office created");
        panel.WaitForElement("#household-picker-settings-select"); // now in two households
    }

    [Fact]
    public async Task The_last_household_offers_no_delete()
    {
        await SignIn();
        var panel = Render<HouseholdPanel>();
        panel.WaitForElement("#household-create");

        panel.FindAll("#household-delete").ShouldBeEmpty();
    }

    [Fact]
    public async Task The_owner_deletes_a_household_after_confirming()
    {
        await SignIn();
        HouseholdServer.Households = [("h1", "Home"), ("h2", "Office")];
        HouseholdServer.ActiveId = "h2";
        HouseholdServer.Name = "Office";
        var panel = Render<HouseholdPanel>();
        panel.WaitForElement("#household-delete");

        panel.Find("#household-delete").Click();
        HouseholdServer.Calls.ShouldNotContain("DELETE households/mine"); // asks first
        panel.Find("#household-delete-warning").TextContent.ShouldContain("Delete Office for everyone in it?");
        panel.Find("#household-delete-confirm").Click();

        panel.WaitForAssertion(() => panel.Find("#household-name").TextContent.ShouldBe("Home"));
        HouseholdServer.Calls.ShouldContain("DELETE households/mine");
        Accounts.Paths.ShouldContain("auth/refresh");
        panel.Find("#household-message").TextContent.ShouldBe("Household deleted. You are now in Home.");
        panel.FindAll("#household-delete").ShouldBeEmpty(); // Home is the last one now
    }

    [Fact]
    public async Task Deleting_can_be_cancelled()
    {
        await SignIn();
        HouseholdServer.Households = [("h1", "Home"), ("h2", "Office")];
        var panel = Render<HouseholdPanel>();
        panel.WaitForElement("#household-delete");

        panel.Find("#household-delete").Click();
        panel.Find("#household-delete-cancel").Click();

        panel.FindAll("#household-delete-confirmation").ShouldBeEmpty();
        HouseholdServer.Calls.ShouldNotContain("DELETE households/mine");
    }
}
