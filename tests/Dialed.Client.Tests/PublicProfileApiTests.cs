using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Dialed.Client.Friends;
using Dialed.Client.Sync;

namespace Dialed.Client.Tests;

/// <summary>The public profile calls of <see cref="FriendsApi"/> (#106): what is sent, and how answers are read.</summary>
public sealed class PublicProfileApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Recorder server = new();

    private FriendsApi Api() => new(new HttpClient(server) { BaseAddress = new Uri("https://api.test/") }, new Tokens());

    [Fact]
    public async Task The_switch_is_read_and_written()
    {
        server.Next = "{\"enabled\":true}";

        (await Api().GetPublicProfileAsync(Ct)).ShouldBeTrue();
        (await Api().SetPublicProfileAsync(false, Ct)).ShouldBeTrue(); // the server's answer is what counts

        server.Requests.Select(r => r.Line).ShouldBe(["GET profile/public", "PUT profile/public"]);
        JsonNode.Parse(server.Requests[1].Body!)!["enabled"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task Search_sends_the_query_escaped_and_reads_the_people()
    {
        server.Next = "[{\"userId\":\"u1\",\"displayName\":\"Zoë & Co\"}]";

        var people = await Api().SearchPeopleAsync("zoë & co", Ct);

        server.Requests.Single().Line.ShouldBe("GET people/search?q=zo%C3%AB%20%26%20co");
        people.ShouldHaveSingleItem().DisplayName.ShouldBe("Zoë & Co");
    }

    [Fact]
    public async Task Following_a_public_profile_posts_to_the_person()
    {
        server.Next = "{\"userId\":\"u1\",\"displayName\":\"Olga\"}";

        (await Api().FollowPublicAsync("u 1", Ct)).DisplayName.ShouldBe("Olga");

        server.Requests.Single().Line.ShouldBe("POST friends/u%201/follow");
    }

    [Fact]
    public async Task A_report_sends_who_what_and_why()
    {
        server.Status = HttpStatusCode.NoContent;

        await Api().ReportAsync("u1", "bean", "b9", "spam", "ads", Ct);

        var request = server.Requests.Single();
        request.Line.ShouldBe("POST reports");
        var body = JsonNode.Parse(request.Body!)!;
        (body["ownerId"]!.GetValue<string>(), body["target"]!.GetValue<string>(), body["itemId"]!.GetValue<string>(), body["reason"]!.GetValue<string>(), body["note"]!.GetValue<string>())
            .ShouldBe(("u1", "bean", "b9", "spam", "ads"));
    }

    [Fact]
    public void A_profile_without_a_relation_is_a_friend_and_only_strangers_get_the_public_view()
    {
        var parsed = System.Text.Json.JsonSerializer.Deserialize<FriendProfileInfo>("{\"userId\":\"u\",\"displayName\":\"A\",\"beans\":1,\"recipes\":2,\"brews\":3,\"followers\":4}", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        parsed.Relation.ShouldBe("following");
        parsed.IsPublicView.ShouldBeFalse();
        (parsed with { Relation = "none" }).IsPublicView.ShouldBeTrue();
        (parsed with { Relation = "requested" }).IsPublicView.ShouldBeTrue();
        (parsed with { Relation = "household" }).IsPublicView.ShouldBeFalse();
    }

    private sealed class Tokens : IAccessTokenProvider
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("token");
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public List<(string Line, string? Body)> Requests { get; } = [];

        public string Next { get; set; } = "{}";

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(($"{request.Method} {request.RequestUri!.PathAndQuery.TrimStart('/')}", request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return Status == HttpStatusCode.NoContent
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : new HttpResponseMessage(Status) { Content = new StringContent(Next, Encoding.UTF8, "application/json") };
        }
    }
}
