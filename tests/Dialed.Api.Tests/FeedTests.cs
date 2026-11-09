using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dialed.Api.Auth;
using Dialed.Api.Friends;
using Dialed.Api.Sync;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Dialed.Api.Tests;

/// <summary>The friends feed (#78): what the people I follow share with me, newest first.</summary>
public sealed class FeedTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> ClientFor(string user)
    {
        var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/auth/dev", new DevLoginRequest(user), Ct);
        var tokens = (await login.Content.ReadFromJsonAsync<TokenResponse>(Ct))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private static async Task<string> Put(HttpClient client, string table, JsonObject entity)
    {
        var id = Guid.CreateVersion7().ToString();
        entity["id"] = id;
        entity["deleted"] = false;
        using var response = await client.PutAsJsonAsync($"/sync/{table}/{id}", entity, Ct);
        response.EnsureSuccessStatusCode();
        return id;
    }

    private static async Task Befriend(HttpClient follower, HttpClient followee, string followerId)
    {
        var code = (await followee.GetFromJsonAsync<JsonObject>("/friends/code", Ct))!["code"]!.GetValue<string>();
        await follower.PostAsJsonAsync("/friends/follow", new FollowRequest(code), Ct);
        await followee.PostAsync($"/friends/requests/{followerId}/accept", null, Ct);
    }

    private static async Task Unfriend(HttpClient followee, string followerId) =>
        await followee.DeleteAsync($"/friends/followers/{followerId}", Ct);

    private static Task<FeedPageDto> Feed(HttpClient client, string query = "") =>
        client.GetFromJsonAsync<FeedPageDto>("/friends/feed" + query, Ct)!;

    private static string[] Titles(FeedPageDto page, string prefix) =>
        [.. page.Entries.Select(e => e.Item["name"]?.GetValue<string>() ?? e.Item["notes"]?.GetValue<string>() ?? "").Where(n => n.StartsWith(prefix, StringComparison.Ordinal))];

    [Fact]
    public async Task The_feed_mixes_brews_and_new_beans_of_friends_newest_first_with_the_bean_name()
    {
        using var alice = await ClientFor("alice");
        using var carol = await ClientFor("carol");
        await Befriend(carol, alice, "dev-carol");
        var now = DateTimeOffset.UtcNow;
        var bean = await Put(alice, "beans", new JsonObject { ["name"] = "F1 Kenya", ["visibility"] = VisibilityRules.Friends });
        var bag = await Put(alice, "bags", new JsonObject { ["beanId"] = bean, ["weightG"] = 250 });
        await Put(alice, "brews", new JsonObject { ["bagId"] = bag, ["methodCode"] = "v60", ["notes"] = "F1 older brew", ["visibility"] = VisibilityRules.Friends, ["brewedAt"] = now.AddHours(-2) });
        await Put(alice, "brews", new JsonObject { ["bagId"] = bag, ["methodCode"] = "v60", ["notes"] = "F1 newest brew", ["visibility"] = VisibilityRules.Friends, ["brewedAt"] = now.AddMinutes(-5) });

        var feed = await Feed(carol);

        var mine = feed.Entries.Where(e => (e.Item["name"] ?? e.Item["notes"])?.GetValue<string>().StartsWith("F1", StringComparison.Ordinal) == true).ToList();
        // A bean counts from the moment it was added (the server stamps that), a brew from when it was brewed.
        mine.Select(e => e.Kind).ShouldBe(["bean", "brew", "brew"]);
        Titles(feed, "F1").ShouldBe(["F1 Kenya", "F1 newest brew", "F1 older brew"]);
        mine.ShouldAllBe(e => e.OwnerId == "dev-alice" && e.OwnerName == "Alice");
        mine[0].BeanName.ShouldBeNull();
        mine[1].BeanName.ShouldBe("F1 Kenya");
        mine[2].BeanName.ShouldBe("F1 Kenya");
        mine[1].At.ShouldBeGreaterThan(mine[2].At);
        await Unfriend(alice, "dev-carol");
    }

    [Fact]
    public async Task Only_what_is_shared_with_friends_by_people_I_follow_shows_up()
    {
        using var alice = await ClientFor("alice");
        using var bob = await ClientFor("bob");
        using var carol = await ClientFor("carol");
        await Befriend(carol, alice, "dev-carol");
        await Put(alice, "beans", new JsonObject { ["name"] = "F2 friends", ["visibility"] = VisibilityRules.Friends });
        await Put(alice, "beans", new JsonObject { ["name"] = "F2 household", ["visibility"] = VisibilityRules.Household });
        await Put(alice, "beans", new JsonObject { ["name"] = "F2 private", ["visibility"] = VisibilityRules.Private });
        await Put(bob, "beans", new JsonObject { ["name"] = "F2 bobs friends bean", ["visibility"] = VisibilityRules.Friends });

        // Carol follows alice only, so bob's bean is not in her feed even though it is shared with friends.
        Titles(await Feed(carol), "F2").ShouldBe(["F2 friends"]);
        await Unfriend(alice, "dev-carol");
    }

    [Fact]
    public async Task Nobody_followed_means_an_empty_feed_and_a_removed_or_blocked_friend_disappears()
    {
        using var alice = await ClientFor("alice");
        using var carol = await ClientFor("carol");
        (await Feed(carol)).Entries.Where(e => e.OwnerId == "dev-alice").ShouldBeEmpty();
        await Befriend(carol, alice, "dev-carol");
        await Put(alice, "beans", new JsonObject { ["name"] = "F3 bean", ["visibility"] = VisibilityRules.Friends });
        Titles(await Feed(carol), "F3").ShouldBe(["F3 bean"]);

        await alice.PostAsync("/friends/blocks/dev-carol", null, Ct);
        Titles(await Feed(carol), "F3").ShouldBeEmpty();
        await alice.DeleteAsync("/friends/blocks/dev-carol", Ct);
    }

    [Fact]
    public async Task Pages_are_cut_at_the_page_size_and_say_when_there_is_more()
    {
        using var alice = await ClientFor("alice");
        using var carol = await ClientFor("carol");
        await Befriend(carol, alice, "dev-carol");
        for (var i = 0; i < 5; i++)
        {
            await Put(alice, "beans", new JsonObject { ["name"] = $"F4 bean {i}", ["visibility"] = VisibilityRules.Friends });
            await Task.Delay(5, Ct);
        }

        var first = await Feed(carol, "?pageSize=2");
        var second = await Feed(carol, "?pageSize=2&skip=2");
        var third = await Feed(carol, "?pageSize=2&skip=4");

        // Newest first: the last bean added comes first.
        Titles(first, "F4").ShouldBe(["F4 bean 4", "F4 bean 3"]);
        first.HasMore.ShouldBeTrue();
        Titles(second, "F4").ShouldBe(["F4 bean 2", "F4 bean 1"]);
        Titles(third, "F4").ShouldBe(["F4 bean 0"]);

        // Other tests leave their own friend-visible beans behind, so "no more" is only certain for a page big enough to hold everything.
        (await Feed(carol, "?pageSize=50")).HasMore.ShouldBeFalse();
        await Unfriend(alice, "dev-carol");
    }

    [Fact]
    public async Task The_feed_needs_a_signed_in_person()
    {
        using var anonymous = factory.CreateClient();

        (await anonymous.GetAsync("/friends/feed", Ct)).StatusCode.ShouldBe(System.Net.HttpStatusCode.Unauthorized);
    }
}
