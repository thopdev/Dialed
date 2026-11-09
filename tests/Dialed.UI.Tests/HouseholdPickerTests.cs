using Bunit;
using Dialed.Client.Account;
using Dialed.UI.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

/// <summary>Picking which household to look at (#73).</summary>
public sealed class HouseholdPickerTests : UiTestContext
{
    private async Task SignIn()
    {
        Tokens.Value = "tkn";
        await Services.GetRequiredService<AuthSession>().SetAsync(new SessionTokens("a", DateTimeOffset.MaxValue, "r", "u1", "Thomas", "h1"), Xunit.TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Nothing_shows_while_signed_out()
    {
        Render<HouseholdPicker>().FindAll(".dialed-household-picker").ShouldBeEmpty();
    }

    [Fact]
    public async Task One_household_needs_no_picker()
    {
        await SignIn();

        var picker = Render<HouseholdPicker>();

        picker.FindAll(".dialed-household-picker").ShouldBeEmpty();
    }

    [Fact]
    public async Task Several_households_show_a_picker_with_the_active_one_selected()
    {
        await SignIn();
        HouseholdServer.Households = [("h1", "Home"), ("h2", "Office")];

        var picker = Render<HouseholdPicker>();

        picker.WaitForElement("select");
        picker.FindAll("option").Select(o => o.TextContent).ShouldBe(["Home", "Office"]);
        picker.FindAll("option")[0].HasAttribute("selected").ShouldBeTrue();
    }

    [Fact]
    public async Task Choosing_another_household_switches_to_it()
    {
        await SignIn();
        HouseholdServer.Households = [("h1", "Home"), ("h2", "Office")];
        var picker = Render<HouseholdPicker>();
        picker.WaitForElement("select");

        picker.Find("select").Change("h2");

        picker.WaitForAssertion(() => HouseholdServer.Calls.ShouldContain("POST households/mine/switch"));
        picker.WaitForAssertion(() => picker.FindAll("option")[1].HasAttribute("selected").ShouldBeTrue());
    }

    [Fact]
    public async Task Waiting_changes_stop_the_switch_and_say_why()
    {
        await SignIn();
        HouseholdServer.Households = [("h1", "Home"), ("h2", "Office")];
        Transport.Offline = true;
        _ = Services.GetRequiredService<Dialed.Client.Sync.SyncCoordinator>(); // builds the engine, which starts watching the repositories for changes
        var recipes = Services.GetRequiredService<Dialed.Client.Data.IRepository<Dialed.Domain.Model.Recipe>>();
        await recipes.SaveAsync(new Dialed.Domain.Model.Recipe { MethodCode = "v60", Name = "Unsynced" }, Xunit.TestContext.Current.CancellationToken);
        var picker = Render<HouseholdPicker>();
        picker.WaitForElement("select");

        picker.Find("select").Change("h2");

        picker.WaitForAssertion(() => picker.Find(".household-picker-message").TextContent.ShouldContain("not been sent"));
        HouseholdServer.Calls.ShouldNotContain("POST households/mine/switch");
    }
}
