using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dialed.Api.Auth;
using Dialed.Api.Friends;
using Dialed.Api.Sync;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.Api.Tests;

public sealed class FriendTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
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

    private static async Task<string> CodeOf(HttpClient client) =>
        (await client.GetFromJsonAsync<FriendCodeDto>("/friends/code", Ct))!.Code;

    private static async Task<string[]> Names(HttpClient client, string path) =>
        [.. (await client.GetFromJsonAsync<PersonDto[]>(path, Ct))!.Select(p => p.DisplayName)];

    private Task<RelationshipProbe> Probe() => Task.FromResult(new RelationshipProbe(factory.Services.GetRequiredService<FriendService>()));

    private sealed record RelationshipProbe(FriendService Service)
    {
        public Task<Relationship> Of(string viewer, string owner) => Service.RelationshipAsync(viewer, owner, Ct);
    }

    // Each test uses its own pair so the shared factory state does not leak between tests: alice/bob, carol, and fresh users via block cleanup.
    [Fact]
    public async Task A_friend_code_is_stable_and_can_be_rotated()
    {
        using var carol = await ClientFor("carol");
        var first = await CodeOf(carol);

        (await CodeOf(carol)).ShouldBe(first);
        using var rotated = await carol.PostAsync("/friends/code/rotate", null, Ct);
        var second = (await rotated.Content.ReadFromJsonAsync<FriendCodeDto>(Ct))!.Code;

        second.ShouldNotBe(first);
        using var alice = await ClientFor("alice");
        (await alice.PostAsJsonAsync("/friends/follow", new FollowRequest(first), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Following_needs_approval_and_then_shows_in_both_lists()
    {
        using var alice = await ClientFor("alice");
        using var carol = await ClientFor("carol");
        var code = await CodeOf(carol);

        (await alice.PostAsJsonAsync("/friends/follow", new FollowRequest(code), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await Names(carol, "/friends/requests")).ShouldContain("Alice");
        (await Names(carol, "/friends/followers")).ShouldNotContain("Alice");
        (await Names(alice, "/friends/following/pending")).ShouldContain("Carol");
        (await (await Probe()).Of("dev-alice", "dev-carol")).ShouldBe(Relationship.Stranger);

        (await carol.PostAsync("/friends/requests/dev-alice/accept", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await Names(carol, "/friends/followers")).ShouldContain("Alice");
        (await Names(alice, "/friends/following")).ShouldContain("Carol");
        (await Names(carol, "/friends/requests")).ShouldNotContain("Alice");
        (await (await Probe()).Of("dev-alice", "dev-carol")).ShouldBe(Relationship.AcceptedFollower);
        (await (await Probe()).Of("dev-carol", "dev-alice")).ShouldBe(Relationship.Stranger);

        // clean up for the other tests
        (await alice.DeleteAsync("/friends/following/dev-carol", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Names(carol, "/friends/followers")).ShouldNotContain("Alice");
    }

    [Fact]
    public async Task A_declined_request_disappears_and_the_asker_can_try_again()
    {
        using var bob = await ClientFor("bob");
        using var carol = await ClientFor("carol");
        var code = await CodeOf(carol);
        await bob.PostAsJsonAsync("/friends/follow", new FollowRequest(code), Ct);

        (await carol.PostAsync("/friends/requests/dev-bob/decline", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await Names(carol, "/friends/requests")).ShouldNotContain("Bob");
        (await Names(bob, "/friends/following/pending")).ShouldNotContain("Carol");
        (await bob.PostAsJsonAsync("/friends/follow", new FollowRequest(code), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await carol.PostAsync("/friends/requests/dev-bob/decline", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Asking_twice_or_following_yourself_is_refused()
    {
        using var alice = await ClientFor("alice");
        using var bob = await ClientFor("bob");
        var code = await CodeOf(bob);

        (await alice.PostAsJsonAsync("/friends/follow", new FollowRequest(await CodeOf(alice)), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await alice.PostAsJsonAsync("/friends/follow", new FollowRequest(code), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await alice.PostAsJsonAsync("/friends/follow", new FollowRequest(code), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await alice.PostAsJsonAsync("/friends/follow", new FollowRequest("NOPE1234"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await alice.DeleteAsync("/friends/following/dev-bob", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task The_followed_person_can_remove_a_follower()
    {
        using var alice = await ClientFor("alice");
        using var carol = await ClientFor("carol");
        await alice.PostAsJsonAsync("/friends/follow", new FollowRequest(await CodeOf(carol)), Ct);
        await carol.PostAsync("/friends/requests/dev-alice/accept", null, Ct);

        (await carol.DeleteAsync("/friends/followers/dev-alice", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await Names(alice, "/friends/following")).ShouldNotContain("Carol");
        (await (await Probe()).Of("dev-alice", "dev-carol")).ShouldBe(Relationship.Stranger);
    }

    [Fact]
    public async Task Blocking_ends_follows_stops_new_requests_and_hides_that_it_happened()
    {
        using var alice = await ClientFor("alice");
        using var carol = await ClientFor("carol");
        var code = await CodeOf(carol);
        await alice.PostAsJsonAsync("/friends/follow", new FollowRequest(code), Ct);
        await carol.PostAsync("/friends/requests/dev-alice/accept", null, Ct);

        (await carol.PostAsync("/friends/blocks/dev-alice", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await Names(carol, "/friends/followers")).ShouldNotContain("Alice");
        (await Names(carol, "/friends/blocks")).ShouldContain("Alice");
        (await (await Probe()).Of("dev-alice", "dev-carol")).ShouldBe(Relationship.Blocked);
        (await (await Probe()).Of("dev-carol", "dev-alice")).ShouldBe(Relationship.Blocked);
        using var again = await alice.PostAsJsonAsync("/friends/follow", new FollowRequest(code), Ct);
        again.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await again.Content.ReadAsStringAsync(Ct)).ShouldNotContain("block");

        (await carol.DeleteAsync("/friends/blocks/dev-alice", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await Probe()).Of("dev-alice", "dev-carol")).ShouldBe(Relationship.Stranger);
    }

    [Fact]
    public async Task A_blocked_person_cannot_even_be_followed_by_the_blocker_and_blocking_yourself_is_refused()
    {
        using var bob = await ClientFor("bob");
        (await bob.PostAsync("/friends/blocks/dev-bob", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await bob.PostAsync("/friends/blocks/nobody", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_endpoints_need_a_signed_in_user()
    {
        using var anonymous = factory.CreateClient();

        (await anonymous.GetAsync("/friends/code", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/friends/followers", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
