using Bunit;
using Dialed.Client.Account;
using Dialed.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

/// <summary>The friends activity feed (#78).</summary>
public sealed class ActivityTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private async Task SignIn()
    {
        Tokens.Value = "tkn";
        await Services.GetRequiredService<AuthSession>().SetAsync(new SessionTokens("a", DateTimeOffset.MaxValue, "r", "me", "Thomas", "h-me"), Ct);
    }

    private static string Brew(string owner, string ownerId, string? bean, DateTimeOffset at, int? rating = null) =>
        $"{{\"kind\":\"brew\",\"ownerId\":\"{ownerId}\",\"ownerName\":\"{owner}\",\"at\":\"{at:O}\",\"beanName\":{(bean is null ? "null" : $"\"{bean}\"")},\"item\":{{\"id\":\"b\",\"methodCode\":\"v60\"{(rating is null ? "" : $",\"rating\":{rating}")}}}}}";

    private static string Bean(string owner, string ownerId, string name, DateTimeOffset at) =>
        $"{{\"kind\":\"bean\",\"ownerId\":\"{ownerId}\",\"ownerName\":\"{owner}\",\"at\":\"{at:O}\",\"beanName\":null,\"item\":{{\"id\":\"x\",\"name\":\"{name}\",\"origin\":{{\"country\":\"Kenya\"}}}}}}";

    private static HttpResponseMessage Feed(bool more, params string[] entries) =>
        TestFriendsServer.Json($"{{\"entries\":[{string.Join(",", entries)}],\"hasMore\":{(more ? "true" : "false")}}}");

    private static DateTimeOffset Noon => new(DateTime.Today.AddHours(12));

    [Fact]
    public void Signed_out_points_to_signing_in()
    {
        var page = Render<Activity>();

        page.Find("#feed-sign-in").GetAttribute("href").ShouldBe("settings#group-account");
        FriendsServer.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Entries_say_who_did_what_with_the_method_the_time_and_a_rating_and_link_to_the_friend()
    {
        FriendsServer.Respond = call => call.StartsWith("GET friends/feed", StringComparison.Ordinal)
            ? Feed(false, Brew("Carol", "u5", "Kenya AA", Noon, rating: 4), Bean("Dave", "u6", "Gesha", Noon.AddDays(-1)))
            : null;
        await SignIn();

        var page = Render<Activity>();

        page.WaitForAssertion(() => page.FindAll("#feed-list .dialed-row").Count.ShouldBe(2));
        var rows = page.FindAll("#feed-list .dialed-row");
        rows[0].QuerySelector(".dialed-row-title")!.TextContent.ShouldBe("Carol brewed Kenya AA");
        rows[0].QuerySelector(".dialed-row-subtitle")!.TextContent.ShouldStartWith("Hario V60 · Today ");
        rows[0].QuerySelector(".dialed-rating-badge")!.GetAttribute("aria-label").ShouldBe("4 of 5 stars");
        rows[0].GetAttribute("href").ShouldBe("people/u5");
        rows[0].QuerySelector(".dialed-avatar")!.GetAttribute("aria-label").ShouldBe("Carol");
        rows[1].QuerySelector(".dialed-row-title")!.TextContent.ShouldBe("Dave added Gesha");
        rows[1].QuerySelector(".dialed-row-subtitle")!.TextContent.ShouldStartWith("Kenya · Yesterday ");
        rows[1].GetAttribute("href").ShouldBe("people/u6");
    }

    [Fact]
    public async Task A_brew_without_a_known_bean_still_reads_naturally()
    {
        FriendsServer.Respond = call => call.StartsWith("GET friends/feed", StringComparison.Ordinal) ? Feed(false, Brew("Carol", "u5", null, Noon)) : null;
        await SignIn();

        var page = Render<Activity>();

        page.WaitForAssertion(() => page.Find("#feed-list .dialed-row-title").TextContent.ShouldBe("Carol brewed a coffee"));
    }

    [Fact]
    public async Task An_empty_feed_explains_what_shows_up_here()
    {
        FriendsServer.Respond = call => call.StartsWith("GET friends/feed", StringComparison.Ordinal) ? Feed(false) : null;
        await SignIn();

        var page = Render<Activity>();

        page.WaitForAssertion(() => page.Find(".dialed-empty-title").TextContent.ShouldBe("Nothing here yet"));
        page.Find("#feed-find-friends").GetAttribute("href").ShouldBe("people");
        page.FindAll("#feed-list").ShouldBeEmpty();
    }

    [Fact]
    public async Task Load_more_appends_the_next_page_and_Refresh_starts_again()
    {
        var firstCalls = 0;
        FriendsServer.Respond = call => call switch
        {
            "GET friends/feed?skip=0&pageSize=20" => ++firstCalls == 1 ? Feed(true, Bean("Dave", "u6", "One", Noon)) : Feed(false, Bean("Dave", "u6", "Fresh", Noon)),
            "GET friends/feed?skip=1&pageSize=20" => Feed(false, Bean("Dave", "u6", "Two", Noon.AddHours(-1))),
            _ => null,
        };
        await SignIn();
        var page = Render<Activity>();
        page.WaitForAssertion(() => page.Find("#feed-more"));

        page.Find("#feed-more").Click();

        page.WaitForAssertion(() => page.FindAll("#feed-list .dialed-row-title").Select(t => t.TextContent).ShouldBe(["Dave added One", "Dave added Two"]));
        page.FindAll("#feed-more").ShouldBeEmpty();

        page.Find("#feed-refresh").Click();

        page.WaitForAssertion(() => page.FindAll("#feed-list .dialed-row-title").Select(t => t.TextContent).ShouldBe(["Dave added Fresh"]));
    }

    [Fact]
    public async Task Offline_the_last_feed_is_shown_with_a_note()
    {
        var calls = 0;
        FriendsServer.Respond = call => call.StartsWith("GET friends/feed", StringComparison.Ordinal)
            ? ++calls == 1 ? Feed(false, Bean("Dave", "u6", "Gesha", Noon)) : new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
            : null;
        await SignIn();
        var page = Render<Activity>();
        page.WaitForAssertion(() => page.FindAll("#feed-list .dialed-row").Count.ShouldBe(1));

        page.Find("#feed-refresh").Click();

        page.WaitForAssertion(() => page.Find("#feed-offline").TextContent.ShouldNotBeEmpty());
        page.FindAll("#feed-list .dialed-row").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Offline_from_the_start_with_nothing_saved_says_so()
    {
        FriendsServer.Respond = call => call.StartsWith("GET friends/feed", StringComparison.Ordinal) ? new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) : null;
        await SignIn();

        var page = Render<Activity>();

        page.WaitForAssertion(() => page.Find("#feed-offline").TextContent.ShouldContain("Could not reach"));
    }

    [Fact]
    public async Task People_links_to_the_feed_only_when_signed_in()
    {
        Render<People>().FindAll("#people-feed").ShouldBeEmpty();
        await SignIn();

        Render<People>().Find("#people-feed").GetAttribute("href").ShouldBe("feed");
    }
}
