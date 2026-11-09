using System.Net;
using Bunit;
using Dialed.Client.Account;
using Dialed.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class FriendsUiTests : UiTestContext
{
    private AuthSession Session => Services.GetRequiredService<AuthSession>();

    private async Task SignIn()
    {
        Tokens.Value = "tkn";
        await Session.SetAsync(new SessionTokens("a", DateTimeOffset.MaxValue, "r", "u1", "Thomas", "h-u1"), Xunit.TestContext.Current.CancellationToken);
    }

    private static HttpResponseMessage People(params (string Id, string Name)[] people) =>
        TestFriendsServer.Json("[" + string.Join(",", people.Select(p => $"{{\"userId\":\"{p.Id}\",\"displayName\":\"{p.Name}\"}}")) + "]");

    [Fact]
    public void Signed_out_asks_to_sign_in()
    {
        var page = Render<People>();

        page.Markup.ShouldContain("Sign in to follow friends");
        FriendsServer.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Shows_the_friend_code_and_the_lists()
    {
        FriendsServer.Respond = call => call switch
        {
            "GET friends/requests" => People(("u2", "Iris")),
            "GET friends/following" => People(("u3", "Carol")),
            "GET friends/following/pending" => People(("u4", "Dave")),
            "GET friends/followers" => People(("u5", "Erin")),
            _ => null,
        };
        await SignIn();

        var page = Render<People>();

        page.WaitForAssertion(() =>
        {
            page.Find("#people-my-code").TextContent.ShouldBe("ABCD2345");
            page.Find(".people-request").TextContent.ShouldContain("Iris");
            page.Find(".people-followed a").GetAttribute("href").ShouldBe("people/u3");
            page.Find(".people-pending").TextContent.ShouldContain("Dave");
            page.Find(".people-follower").TextContent.ShouldContain("Erin");
        });
    }

    [Fact]
    public async Task Following_by_code_sends_the_request_and_confirms()
    {
        FriendsServer.Respond = call => call == "POST friends/follow" ? TestFriendsServer.Json("{\"userId\":\"u2\",\"displayName\":\"Iris\"}") : null;
        await SignIn();
        var page = Render<People>();
        page.WaitForAssertion(() => page.Find("#people-my-code"));

        page.Find("#people-follow-code").Input("WXYZ2345");
        page.Find("#people-follow form, form").Submit();

        page.WaitForAssertion(() => page.Find("#people-message").TextContent.ShouldContain("Request sent"));
        FriendsServer.Calls.ShouldContain("POST friends/follow");
    }

    [Fact]
    public async Task A_refused_request_shows_the_reason()
    {
        FriendsServer.Respond = call => call == "POST friends/follow"
            ? TestFriendsServer.Json("{\"detail\":\"That friend code is not valid.\"}", HttpStatusCode.NotFound)
            : null;
        await SignIn();
        var page = Render<People>();
        page.WaitForAssertion(() => page.Find("#people-my-code"));
        page.Find("#people-follow-code").Input("NOPE");

        page.Find("form").Submit();

        page.WaitForAssertion(() => page.Find("#people-message").TextContent.ShouldContain("not valid"));
    }

    [Fact]
    public async Task Accepting_a_request_calls_the_server_and_reloads()
    {
        var accepted = false;
        FriendsServer.Respond = call =>
        {
            if (call == "POST friends/requests/u2/accept")
            {
                accepted = true;
                return TestFriendsServer.Json("{\"userId\":\"u2\",\"displayName\":\"Iris\"}");
            }

            return call == "GET friends/requests" && !accepted ? People(("u2", "Iris")) : null;
        };
        await SignIn();
        var page = Render<People>();
        page.WaitForAssertion(() => page.Find(".people-accept"));

        page.Find(".people-accept").Click();

        page.WaitForAssertion(() => page.FindAll(".people-request").ShouldBeEmpty());
        accepted.ShouldBeTrue();
    }

    [Fact]
    public async Task Decline_unfollow_remove_and_block_use_their_endpoints()
    {
        FriendsServer.Respond = call => call switch
        {
            "GET friends/requests" => People(("u2", "Iris")),
            "GET friends/following" => People(("u3", "Carol")),
            "GET friends/followers" => People(("u5", "Erin")),
            _ => null,
        };
        await SignIn();
        var page = Render<People>();
        page.WaitForAssertion(() => page.Find(".people-decline"));

        page.Find(".people-decline").Click();
        page.Find(".people-unfollow").Click();
        page.Find(".people-remove").Click();
        page.Find(".people-block").Click();

        page.WaitForAssertion(() =>
        {
            FriendsServer.Calls.ShouldContain("POST friends/requests/u2/decline");
            FriendsServer.Calls.ShouldContain("DELETE friends/following/u3");
            FriendsServer.Calls.ShouldContain("DELETE friends/followers/u5");
            FriendsServer.Calls.ShouldContain("POST friends/blocks/u5");
        });
    }

    [Fact]
    public async Task A_down_server_shows_an_offline_note_and_retry()
    {
        FriendsServer.Respond = _ => throw new HttpRequestException("offline");
        await SignIn();

        var page = Render<People>();

        page.WaitForAssertion(() => page.Find("#people-offline").TextContent.ShouldContain("Could not reach"));
        page.FindAll("#people-retry").Count.ShouldBe(1);
    }

    // ---- friend profile ---------------------------------------------------------------------------

    private static HttpResponseMessage Shared(int total, bool more, params string[] names) =>
        TestFriendsServer.Json($"{{\"items\":[{string.Join(",", names.Select(n => $"{{\"id\":\"{n}\",\"name\":\"{n}\"}}"))}],\"total\":{total},\"hasMore\":{(more ? "true" : "false")}}}");

    private const string AliceProfile = "{\"userId\":\"u3\",\"displayName\":\"Alice\",\"beans\":2,\"recipes\":1,\"brews\":0,\"followers\":4}";

    [Fact]
    public async Task The_profile_shows_name_counts_and_shared_beans_then_switches_tab()
    {
        FriendsServer.Respond = call => call switch
        {
            "GET friends/u3/profile" => TestFriendsServer.Json(AliceProfile),
            "GET friends/u3/shared/beans?skip=0&pageSize=20" => Shared(2, false, "Gesha", "Santos"),
            "GET friends/u3/shared/recipes?skip=0&pageSize=20" => Shared(1, false, "Morning V60"),
            _ => null,
        };
        await SignIn();

        var page = Render<FriendProfile>(p => p.Add(x => x.UserId, "u3"));

        page.WaitForAssertion(() =>
        {
            page.Find("#friend-name").TextContent.ShouldBe("Alice");
            page.Find("#friend-counts").TextContent.ShouldContain("2 beans");
            page.FindAll(".friend-item").Select(i => i.TextContent).ShouldBe(["Gesha", "Santos"]);
        });

        page.Find("#friend-tab-recipes").Click();

        page.WaitForAssertion(() => page.FindAll(".friend-item").Select(i => i.TextContent).ShouldBe(["Morning V60"]));
    }

    [Fact]
    public async Task Load_more_appends_the_next_page()
    {
        FriendsServer.Respond = call => call switch
        {
            "GET friends/u3/profile" => TestFriendsServer.Json(AliceProfile),
            "GET friends/u3/shared/beans?skip=0&pageSize=20" => Shared(3, true, "One", "Two"),
            "GET friends/u3/shared/beans?skip=2&pageSize=20" => Shared(3, false, "Three"),
            _ => null,
        };
        await SignIn();
        var page = Render<FriendProfile>(p => p.Add(x => x.UserId, "u3"));
        page.WaitForAssertion(() => page.Find("#friend-more"));

        page.Find("#friend-more").Click();

        page.WaitForAssertion(() =>
        {
            page.FindAll(".friend-item").Select(i => i.TextContent).ShouldBe(["One", "Two", "Three"]);
            page.FindAll("#friend-more").ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task An_empty_tab_and_a_profile_you_may_not_see_have_clear_messages()
    {
        FriendsServer.Respond = call => call switch
        {
            "GET friends/u3/profile" => TestFriendsServer.Json(AliceProfile),
            "GET friends/u3/shared/beans?skip=0&pageSize=20" => Shared(0, false),
            "GET friends/u9/profile" => TestFriendsServer.Json("{\"detail\":\"Not found.\"}", HttpStatusCode.NotFound),
            _ => null,
        };
        await SignIn();

        var empty = Render<FriendProfile>(p => p.Add(x => x.UserId, "u3"));
        var hidden = Render<FriendProfile>(p => p.Add(x => x.UserId, "u9"));

        empty.WaitForAssertion(() => empty.Markup.ShouldContain("Nothing is shared here yet"));
        hidden.WaitForAssertion(() => hidden.Markup.ShouldContain("not available"));
    }

    [Fact]
    public async Task Offline_the_last_seen_profile_and_page_are_still_shown()
    {
        var online = true;
        FriendsServer.Respond = call =>
        {
            if (!online)
            {
                throw new HttpRequestException("offline");
            }

            return call switch
            {
                "GET friends/u3/profile" => TestFriendsServer.Json(AliceProfile),
                "GET friends/u3/shared/beans?skip=0&pageSize=20" => Shared(1, false, "Gesha"),
                _ => null,
            };
        };
        await SignIn();
        var first = Render<FriendProfile>(p => p.Add(x => x.UserId, "u3"));
        first.WaitForAssertion(() => first.Find(".friend-item"));
        online = false;

        var second = Render<FriendProfile>(p => p.Add(x => x.UserId, "u3"));

        second.WaitForAssertion(() =>
        {
            second.Find("#friend-offline").TextContent.ShouldContain("No connection");
            second.Find(".friend-item").TextContent.ShouldBe("Gesha");
        });
    }

    [Fact]
    public async Task Offline_without_anything_saved_offers_a_retry()
    {
        FriendsServer.Respond = _ => throw new HttpRequestException("offline");
        await SignIn();

        var page = Render<FriendProfile>(p => p.Add(x => x.UserId, "u3"));

        page.WaitForAssertion(() => page.FindAll("#friend-retry").Count.ShouldBe(1));
    }
}
