using System.Net;
using Bunit;
using Dialed.Client.Admin;
using Dialed.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

/// <summary>The owner's moderation screen (#106, #105).</summary>
public sealed class AdminUiTests : UiTestContext
{
    private AdminApi Api => Services.GetRequiredService<AdminApi>();

    private IRenderedComponent<Admin> Unlocked(string tab = "reports")
    {
        var page = Render<Admin>();
        page.Find("#admin-key").Input(TestAdminServer.Key);
        page.Find("#admin-unlock-form").Submit();
        page.WaitForAssertion(() => page.Find("#admin-lock"));
        if (tab != "reports")
        {
            page.Find($"#admin-tab-{tab}").Click();
            page.WaitForAssertion(() => page.Find($"#admin-tab-{tab}").ClassList.ShouldContain("selected"));
            page.WaitForAssertion(() => page.FindAll("#admin-reports").ShouldBeEmpty());
        }

        return page;
    }

    // ---- Unlocking ----------------------------------------------------------------------------

    [Fact]
    public void The_screen_starts_locked_and_asks_nothing_of_the_server()
    {
        var page = Render<Admin>();

        page.Find("#admin-key").GetAttribute("type").ShouldBe("password");
        page.Find("#admin-unlock").HasAttribute("disabled").ShouldBeTrue();
        page.FindAll("#admin-reports").ShouldBeEmpty();
        AdminServer.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void A_wrong_key_says_so_stays_locked_and_forgets_what_was_typed()
    {
        var page = Render<Admin>();

        page.Find("#admin-key").Input("nope");
        page.Find("#admin-unlock-form").Submit();

        page.WaitForAssertion(() => page.Find("#admin-problem").TextContent.ShouldBe("That key is not right."));
        page.Find("#admin-problem").GetAttribute("role").ShouldBe("alert");
        page.Find("#admin-key").GetAttribute("value").ShouldBeNullOrEmpty();
        Api.IsUnlocked.ShouldBeFalse();
    }

    [Fact]
    public void A_server_without_an_admin_key_says_admin_is_not_switched_on()
    {
        AdminServer.NotEnabled = true;
        var page = Render<Admin>();

        page.Find("#admin-key").Input("anything");
        page.Find("#admin-unlock-form").Submit();

        page.WaitForAssertion(() => page.Find("#admin-problem").TextContent.ShouldContain("not switched on"));
    }

    [Fact]
    public void The_right_key_opens_the_reports_and_every_call_carries_the_key()
    {
        var page = Unlocked();

        page.Find("#admin-reports .admin-report").TextContent.ShouldContain("Spam");
        page.Find("#admin-reports .admin-report").TextContent.ShouldContain("Spammy");
        page.Find("#admin-reports .admin-note").TextContent.ShouldBe("ads everywhere");
        AdminServer.KeysSent.ShouldAllBe(k => k == TestAdminServer.Key);
        Api.IsUnlocked.ShouldBeTrue();
    }

    [Fact]
    public void Locking_forgets_the_key_and_shows_the_form_again()
    {
        var page = Unlocked();

        page.Find("#admin-lock").Click();

        page.Find("#admin-unlock-form").ShouldNotBeNull();
        Api.IsUnlocked.ShouldBeFalse();
        page.FindAll("#admin-reports").ShouldBeEmpty();
    }

    [Fact]
    public void A_key_the_server_stops_accepting_locks_the_screen_again()
    {
        var page = Unlocked();
        AdminServer.Fail = call => call.StartsWith("POST") ? HttpStatusCode.Unauthorized : null;

        page.Find(".admin-suspend").Click();

        page.WaitForAssertion(() => page.Find("#admin-problem").TextContent.ShouldContain("not right"));
        page.FindAll("#admin-reports").ShouldBeEmpty();
        Api.IsUnlocked.ShouldBeFalse();
    }

    // ---- Reports and people -------------------------------------------------------------------

    [Fact]
    public void A_report_can_lead_to_suspending_its_owner()
    {
        var page = Unlocked();
        page.Find(".admin-suspend").GetAttribute("aria-label").ShouldBe("Suspend Spammy");

        page.Find(".admin-suspend").Click();

        page.WaitForAssertion(() => page.Find("#admin-message").TextContent.ShouldContain("Spammy is suspended"));
        AdminServer.Calls.ShouldContain("POST admin/users/u9/suspend");
    }

    [Fact]
    public void With_no_reports_the_screen_says_so()
    {
        AdminServer.ReportsJson = "[]";

        Unlocked().Find("#admin-reports-empty").TextContent.ShouldBe("No reports.");
    }

    [Fact]
    public void The_people_tab_lists_who_is_suspended_and_can_restore_them()
    {
        var page = Unlocked("people");

        page.Find("#admin-suspended .admin-person").TextContent.ShouldContain("Banned");
        page.Find(".admin-restore").GetAttribute("aria-label").ShouldBe("Restore Banned");
        page.Find(".admin-restore").Click();

        page.WaitForAssertion(() => page.Find("#admin-message").TextContent.ShouldBe("Restored."));
        AdminServer.Calls.ShouldContain("POST admin/users/u7/restore");
    }

    [Fact]
    public void A_person_can_be_suspended_by_id()
    {
        var page = Unlocked("people");
        page.Find("#admin-suspend-typed").HasAttribute("disabled").ShouldBeTrue();

        page.Find("#admin-user-id").Input("  u42 ");
        page.Find("#admin-suspend-form").Submit();

        page.WaitForAssertion(() => AdminServer.Calls.ShouldContain("POST admin/users/u42/suspend"));
        page.WaitForAssertion(() => page.Find("#admin-user-id").GetAttribute("value").ShouldBeNullOrEmpty());
    }

    // ---- The catalog --------------------------------------------------------------------------

    [Fact]
    public void The_catalog_tab_lists_entries_with_how_many_people_stand_behind_them()
    {
        var page = Unlocked("catalog");

        page.FindAll("#admin-entries .admin-entry").Select(e => e.TextContent).ShouldContain(t => t.Contains("Thin Bean") && t.Contains("2 people"));
        page.FindAll(".admin-entry").Count.ShouldBe(3);
    }

    [Fact]
    public void An_entry_can_be_hidden()
    {
        var page = Unlocked("catalog");

        page.Find("[data-entry='b-1'] .admin-hide").GetAttribute("aria-label").ShouldBe("Hide Thin Bean from the catalog");
        page.Find("[data-entry='b-1'] .admin-hide").Click();

        page.WaitForAssertion(() => page.Find("#admin-message").TextContent.ShouldBe("Thin Bean is hidden."));
        AdminServer.Calls.ShouldContain("POST admin/catalog/entries/b-1/hide");
    }

    [Fact]
    public void Merging_picks_an_entry_then_only_offers_entries_of_the_same_kind()
    {
        var page = Unlocked("catalog");

        page.Find("[data-entry='b-1'] .admin-merge").Click();

        page.Find("#admin-merging").TextContent.ShouldContain("Merging Thin Bean");
        page.FindAll(".admin-merge-into").Select(b => b.GetAttribute("aria-label")).ShouldBe(["Merge Thin Bean into Other Bean"]); // not into itself, not into the roaster
        page.Find(".admin-merge-into").Click();

        page.WaitForAssertion(() => page.Find("#admin-message").TextContent.ShouldBe("Thin Bean was merged into Other Bean."));
        AdminServer.Calls.ShouldContain("POST admin/catalog/merge");
        page.FindAll("#admin-merging").ShouldBeEmpty();
    }

    [Fact]
    public void A_merge_can_be_cancelled()
    {
        var page = Unlocked("catalog");
        page.Find("[data-entry='b-1'] .admin-merge").Click();

        page.Find("#admin-merge-cancel").Click();

        page.FindAll("#admin-merging").ShouldBeEmpty();
        page.FindAll(".admin-merge-into").ShouldBeEmpty();
        AdminServer.Calls.ShouldNotContain("POST admin/catalog/merge");
    }

    [Fact]
    public void Rebuilding_says_what_came_out()
    {
        var page = Unlocked("catalog");

        page.Find("#admin-rebuild").Click();

        page.WaitForAssertion(() => page.Find("#admin-message").TextContent.ShouldBe("Rebuilt: 3 roasters, 8 beans."));
    }

    [Fact]
    public void An_entry_that_is_gone_is_reported_and_the_screen_stays()
    {
        var page = Unlocked("catalog");
        AdminServer.Fail = call => call.Contains("/hide") ? HttpStatusCode.NotFound : null;

        page.Find("[data-entry='b-1'] .admin-hide").Click();

        page.WaitForAssertion(() => page.Find("#admin-problem").TextContent.ShouldBe("That no longer exists."));
        page.FindAll(".admin-entry").Count.ShouldBe(3);
    }

    [Fact]
    public void Too_many_tries_and_no_network_have_their_own_messages()
    {
        var page = Render<Admin>();
        AdminServer.Fail = _ => HttpStatusCode.TooManyRequests;
        page.Find("#admin-key").Input(TestAdminServer.Key);
        page.Find("#admin-unlock-form").Submit();

        page.WaitForAssertion(() => page.Find("#admin-problem").TextContent.ShouldContain("Too many tries"));
    }
}
