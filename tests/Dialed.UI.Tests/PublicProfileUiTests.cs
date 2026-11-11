using System.Net;
using System.Text.Json;
using Bunit;
using Dialed.Client.Account;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Dialed.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

/// <summary>Public profiles (#106): the switch, finding people, a stranger's view of a profile, reports and the visibility pickers.</summary>
public sealed class PublicProfileUiTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private async Task SignIn()
    {
        Tokens.Value = "tkn";
        await Services.GetRequiredService<AuthSession>().SetAsync(new SessionTokens("a", DateTimeOffset.MaxValue, "r", "u1", "Thomas", "h-u1"), Ct);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => TestFriendsServer.Json(json, status);

    // ---- The switch ---------------------------------------------------------------------------

    [Fact]
    public void Signed_out_people_are_asked_to_sign_in()
    {
        var panel = Render<PublicProfilePanel>();

        panel.Find("#public-profile-status").TextContent.ShouldContain("Sign in");
        panel.FindAll("#public-profile-switch").ShouldBeEmpty();
        FriendsServer.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_profile_starts_private_and_the_switch_turns_it_on_and_off()
    {
        FriendsServer.Respond = call => call switch
        {
            "GET profile/public" => Json("{\"enabled\":false}"),
            "PUT profile/public" => Json("{\"enabled\":true}"),
            _ => null,
        };
        await SignIn();
        var panel = Render<PublicProfilePanel>();
        panel.WaitForAssertion(() => panel.Find("#public-profile-status").TextContent.ShouldContain("private"));
        panel.Find("#public-profile-switch").HasAttribute("checked").ShouldBeFalse();

        panel.Find("#public-profile-switch").Change(true);

        panel.WaitForAssertion(() => panel.Find("#public-profile-status").TextContent.ShouldContain("is public"));
        FriendsServer.Calls.ShouldContain("PUT profile/public");
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_says_so_and_can_be_retried()
    {
        FriendsServer.Respond = call => call == "GET profile/public" ? Json("{}", HttpStatusCode.InternalServerError) : null;
        await SignIn();

        var panel = Render<PublicProfilePanel>();

        panel.WaitForAssertion(() => panel.Find("#public-profile-status").TextContent.ShouldContain("could not be reached"));
        panel.Find("#public-profile-retry").ShouldNotBeNull();
    }

    // ---- Find people --------------------------------------------------------------------------

    [Fact]
    public async Task Search_needs_two_letters_and_lists_what_the_server_finds()
    {
        FriendsServer.Respond = call => call == "GET people/search?q=zo" ? Json("[{\"userId\":\"u7\",\"displayName\":\"Zoe\"},{\"userId\":\"u8\",\"displayName\":\"Zorro\"}]") : null;
        await SignIn();
        var page = Render<FindPeople>();

        page.Find("#people-find-input").Input("z");
        page.Find("#people-find-submit").HasAttribute("disabled").ShouldBeTrue();
        page.Find("#people-find-input").Input("zo");
        page.Find("#people-find-submit").HasAttribute("disabled").ShouldBeFalse();
        page.Find("#people-find-submit").Click();

        page.WaitForAssertion(() => page.FindAll(".people-find-row").Select(a => a.GetAttribute("href")).ShouldBe(["people/u7", "people/u8"]));
        FriendsServer.Calls.Count(c => c.StartsWith("GET people/search")).ShouldBe(1);
    }

    [Fact]
    public async Task Search_says_so_when_nobody_matches()
    {
        FriendsServer.Respond = call => call.StartsWith("GET people/search") ? Json("[]") : null;
        await SignIn();
        var page = Render<FindPeople>();

        page.Find("#people-find-input").Input("nobody");
        page.Find("#people-find-submit").Click();

        page.WaitForAssertion(() => page.Find("#people-find-none").TextContent.ShouldBe("Nobody found."));
    }

    [Fact]
    public async Task Search_offline_says_so()
    {
        FriendsServer.Respond = call => call.StartsWith("GET people/search") ? Json("{}", HttpStatusCode.InternalServerError) : null;
        await SignIn();
        var page = Render<FindPeople>();

        page.Find("#people-find-input").Input("zo");
        page.Find("#people-find-submit").Click();

        page.WaitForAssertion(() => page.Find("#people-find-offline"));
    }

    [Fact]
    public async Task Find_people_is_on_the_People_screen()
    {
        await SignIn();

        Render<People>().Find("#people-find").ShouldNotBeNull();
    }

    // ---- A stranger looks at a public profile -------------------------------------------------

    private const string StrangerView = "{\"userId\":\"u3\",\"displayName\":\"Olga\",\"beans\":2,\"recipes\":1,\"brews\":0,\"followers\":0,\"relation\":\"none\"}";

    private async Task<IRenderedComponent<FriendProfile>> PublicProfile(string profile = StrangerView)
    {
        FriendsServer.Respond = call => call switch
        {
            "GET friends/u3/profile" => Json(profile),
            "GET friends/u3/shared/beans?skip=0&pageSize=20" => Json("{\"items\":[{\"id\":\"b1\",\"name\":\"S1 Kenya\"}],\"total\":1,\"hasMore\":false}"),
            "POST friends/u3/follow" => Json("{\"userId\":\"u3\",\"displayName\":\"Olga\"}"),
            _ => null,
        };
        await SignIn();
        var page = Render<FriendProfile>(p => p.Add(x => x.UserId, "u3"));
        page.WaitForAssertion(() => page.Find("#friend-name"));
        return page;
    }

    [Fact]
    public async Task A_public_profile_shows_only_beans_and_recipes_and_no_brews_tab()
    {
        var page = await PublicProfile();

        page.Find("#friend-counts").TextContent.ShouldBe("2 beans · 1 recipes");
        page.FindAll("[role=tab]").Select(t => t.Id).ShouldBe(["friend-tab-beans", "friend-tab-recipes"]);
    }

    [Fact]
    public async Task A_stranger_can_follow_from_the_profile_without_a_code()
    {
        var page = await PublicProfile();

        page.Find("#friend-follow").Click();

        page.WaitForAssertion(() => page.Find("#friend-requested").TextContent.ShouldContain("Request sent"));
        FriendsServer.Calls.ShouldContain("POST friends/u3/follow");
        page.FindAll("#friend-follow").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_follow_that_fails_says_so()
    {
        var page = await PublicProfile();
        FriendsServer.Respond = call => call == "POST friends/u3/follow" ? Json("{}", HttpStatusCode.InternalServerError) : null;

        page.Find("#friend-follow").Click();

        page.WaitForAssertion(() => page.Find("#friend-follow-failed"));
    }

    [Fact]
    public async Task A_request_that_is_waiting_shows_that_instead_of_the_button()
    {
        var page = await PublicProfile(StrangerView.Replace("\"none\"", "\"requested\""));

        page.Find("#friend-requested").ShouldNotBeNull();
        page.FindAll("#friend-follow").ShouldBeEmpty();
    }

    [Fact]
    public async Task Copying_a_public_item_works_like_a_friends_item()
    {
        var page = await PublicProfile();

        page.WaitForAssertion(() => page.Find(".friend-copy").GetAttribute("aria-label")!.ShouldContain("S1 Kenya"));
    }

    // ---- Reports ------------------------------------------------------------------------------

    [Fact]
    public async Task A_profile_can_be_reported_with_a_reason_and_a_note()
    {
        var page = await PublicProfile();
        FriendsServer.Calls.Clear();
        FriendsServer.Respond = call => call == "POST reports" ? Json("{}", HttpStatusCode.NoContent) : null;

        page.Find("#friend-actions .report-open").Click();
        page.Find("#friend-actions .report-reason").Change("offensive");
        page.Find("#friend-actions .report-note").Change("rude name");
        page.Find("#friend-actions .report-form").Submit();

        page.WaitForAssertion(() => page.Find("#friend-actions .report-sent").TextContent.ShouldContain("We received your report"));
        FriendsServer.Calls.ShouldContain("POST reports");
    }

    [Fact]
    public async Task A_bean_can_be_reported_from_its_row()
    {
        var page = await PublicProfile();
        FriendsServer.Respond = call => call == "POST reports" ? Json("{}", HttpStatusCode.NoContent) : null;

        page.WaitForAssertion(() => page.Find(".friend-row .report-open").GetAttribute("aria-label").ShouldBe("Report S1 Kenya"));
        page.Find(".friend-row .report-open").Click();
        page.Find(".friend-row .report-form").Submit();

        page.WaitForAssertion(() => page.Find(".friend-row .report-sent"));
    }

    [Fact]
    public async Task A_report_that_fails_can_be_sent_again()
    {
        var page = await PublicProfile();
        FriendsServer.Respond = call => call == "POST reports" ? Json("{}", HttpStatusCode.InternalServerError) : null;
        page.Find("#friend-actions .report-open").Click();

        page.Find("#friend-actions .report-form").Submit();

        page.WaitForAssertion(() => page.Find("#friend-actions .report-failed").GetAttribute("role").ShouldBe("alert"));
        page.Find("#friend-actions .report-send").HasAttribute("disabled").ShouldBeFalse();
    }

    [Fact]
    public async Task Your_own_profile_and_your_household_have_no_report_or_follow_buttons()
    {
        foreach (var relation in new[] { "self", "household" })
        {
            var page = await PublicProfile(StrangerView.Replace("\"none\"", $"\"{relation}\""));
            page.FindAll("#friend-actions").ShouldBeEmpty();
        }
    }

    // ---- The visibility pickers ---------------------------------------------------------------

    [Fact]
    public void Beans_and_recipes_can_be_made_public_with_an_explanation()
    {
        var bean = Render<BeanForm>();
        bean.FindAll("#bean-visibility option").Select(o => o.GetAttribute("value")).ShouldContain("Public");
        bean.FindAll("#bean-visibility-hint").ShouldBeEmpty();

        bean.Find("#bean-visibility").Change("Public");

        bean.Find("#bean-visibility-hint").TextContent.ShouldContain("Everyone who uses Dialed");
        bean.Find("#bean-visibility option[value=Public]").TextContent.ShouldBe("Everyone on Dialed");
        Render<RecipeForm>().FindAll("#recipe-visibility option").Select(o => o.GetAttribute("value")).ShouldContain("Public");
    }

    [Fact]
    public void Brews_and_the_default_for_new_items_cannot_be_public()
    {
        Render<ProfileForm>().FindAll("#profile-visibility option").Select(o => o.GetAttribute("value")).ShouldBe(["Private", "Household", "Friends"]);
    }
}
