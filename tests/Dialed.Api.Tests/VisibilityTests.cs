using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dialed.Api.Auth;
using Dialed.Api.Sync;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Dialed.Api.Tests;

public sealed class VisibilityTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- the rules: every visibility against every relationship -----------------------------------

    [Theory]
    [InlineData(VisibilityRules.Private, Relationship.Author, true)]
    [InlineData(VisibilityRules.Private, Relationship.HouseholdMember, false)]
    [InlineData(VisibilityRules.Private, Relationship.AcceptedFollower, false)]
    [InlineData(VisibilityRules.Private, Relationship.Stranger, false)]
    [InlineData(VisibilityRules.Private, Relationship.Blocked, false)]
    [InlineData(VisibilityRules.Household, Relationship.Author, true)]
    [InlineData(VisibilityRules.Household, Relationship.HouseholdMember, true)]
    [InlineData(VisibilityRules.Household, Relationship.AcceptedFollower, false)]
    [InlineData(VisibilityRules.Household, Relationship.Stranger, false)]
    [InlineData(VisibilityRules.Household, Relationship.Blocked, false)]
    [InlineData(VisibilityRules.Friends, Relationship.Author, true)]
    [InlineData(VisibilityRules.Friends, Relationship.HouseholdMember, true)]
    [InlineData(VisibilityRules.Friends, Relationship.AcceptedFollower, true)]
    [InlineData(VisibilityRules.Friends, Relationship.Stranger, false)]
    [InlineData(VisibilityRules.Friends, Relationship.Blocked, false)]
    public void Who_can_read_what(int visibility, Relationship relationship, bool expected) =>
        VisibilityRules.CanRead(visibility, relationship).ShouldBe(expected);

    [Fact]
    public void Visibility_is_read_from_a_number_or_a_name_and_defaults_to_the_household()
    {
        VisibilityRules.Of(new JsonObject { ["visibility"] = 0 }).ShouldBe(VisibilityRules.Private);
        VisibilityRules.Of(new JsonObject { ["visibility"] = 2 }).ShouldBe(VisibilityRules.Friends);
        VisibilityRules.Of(new JsonObject { ["visibility"] = "private" }).ShouldBe(VisibilityRules.Private);
        VisibilityRules.Of(new JsonObject { ["visibility"] = "Friends" }).ShouldBe(VisibilityRules.Friends);
        VisibilityRules.Of(new JsonObject()).ShouldBe(VisibilityRules.Household);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(-1)]
    [InlineData("secret")]
    public void An_unknown_visibility_is_treated_as_private(object value)
    {
        var entity = new JsonObject { ["visibility"] = JsonValue.Create(value) };

        VisibilityRules.Of(entity).ShouldBe(VisibilityRules.Private);
    }

    // ---- through the API: alice and bob share a household -------------------------------------------

    private async Task<HttpClient> ClientFor(string user)
    {
        var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/auth/dev", new DevLoginRequest(user), Ct);
        var tokens = (await login.Content.ReadFromJsonAsync<TokenResponse>(Ct))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private static async Task<HttpResponseMessage> Push(HttpClient client, string id, int? visibility, string? ifMatch = null)
    {
        var entity = new JsonObject { ["id"] = id, ["name"] = "Gesha", ["deleted"] = false };
        if (visibility is { } v)
        {
            entity["visibility"] = v;
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/sync/beans/{id}") { Content = JsonContent.Create(entity) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<string[]> Ids(HttpClient client)
    {
        var page = await client.GetFromJsonAsync<SyncPageDto>("/sync/beans?pageSize=500", Ct);
        return [.. page!.Records.Select(r => r.Entity["id"]!.GetValue<string>())];
    }

    private static string Id() => Guid.CreateVersion7().ToString();

    [Fact]
    public async Task A_private_item_is_only_pulled_by_its_author()
    {
        using var alice = await ClientFor("alice");
        using var bob = await ClientFor("bob");
        var id = Id();
        (await Push(alice, id, VisibilityRules.Private)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await Ids(alice)).ShouldContain(id);
        (await Ids(bob)).ShouldNotContain(id);
    }

    [Theory]
    [InlineData(VisibilityRules.Household)]
    [InlineData(VisibilityRules.Friends)]
    [InlineData(null)]
    public async Task Household_and_friends_items_are_pulled_by_other_members(int? visibility)
    {
        using var alice = await ClientFor("alice");
        using var bob = await ClientFor("bob");
        var id = Id();
        (await Push(alice, id, visibility)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await Ids(bob)).ShouldContain(id);
    }

    [Fact]
    public async Task Another_member_cannot_overwrite_a_private_item_and_gets_no_hint_that_it_exists()
    {
        using var alice = await ClientFor("alice");
        using var bob = await ClientFor("bob");
        var id = Id();
        await Push(alice, id, VisibilityRules.Private);

        using var attempt = await Push(bob, id, VisibilityRules.Household, ifMatch: "1");

        attempt.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await attempt.Content.ReadAsStringAsync(Ct)).ShouldNotContain("Gesha");
        (await Ids(alice)).ShouldContain(id);
    }

    [Fact]
    public async Task The_author_can_make_an_item_private_later_and_it_disappears_for_others()
    {
        using var alice = await ClientFor("alice");
        using var bob = await ClientFor("bob");
        var id = Id();
        await Push(alice, id, VisibilityRules.Household);
        (await Ids(bob)).ShouldContain(id);

        using var change = await Push(alice, id, VisibilityRules.Private, ifMatch: "1");

        change.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Ids(bob)).ShouldNotContain(id);
    }

    [Fact]
    public async Task A_private_item_does_not_break_paging_for_others()
    {
        using var alice = await ClientFor("alice");
        using var bob = await ClientFor("bob");
        var visible = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var privateId = Id();
            var openId = Id();
            await Push(alice, privateId, VisibilityRules.Private);
            await Push(alice, openId, VisibilityRules.Household);
            visible.Add(openId);
        }

        var ids = await Ids(bob);

        foreach (var id in visible)
        {
            ids.ShouldContain(id);
        }
    }
}
