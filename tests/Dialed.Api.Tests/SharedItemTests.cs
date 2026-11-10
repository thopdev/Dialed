using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dialed.Api.Auth;
using Dialed.Api.Friends;
using Dialed.Api.Sync;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Dialed.Api.Tests;

/// <summary>One shared bean or recipe, for copying it to my household (#77): same visibility rules as the lists, plus the names that live in the owner's household.</summary>
public sealed class SharedItemTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
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
        entity["deleted"] = entity["deleted"] ?? false;
        using var response = await client.PutAsJsonAsync($"/sync/{table}/{id}", entity, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return id;
    }

    /// <summary>Carol follows alice and alice accepts, so carol is an accepted follower.</summary>
    private static async Task Befriend(HttpClient follower, HttpClient followee, string followerId)
    {
        var code = (await followee.GetFromJsonAsync<JsonObject>("/friends/code", Ct))!["code"]!.GetValue<string>();
        await follower.PostAsJsonAsync("/friends/follow", new FollowRequest(code), Ct);
        await followee.PostAsync($"/friends/requests/{followerId}/accept", null, Ct);
    }

    [Fact]
    public async Task A_follower_gets_a_friends_bean_with_the_roaster_name_and_the_owner_name()
    {
        using var alice = await ClientFor("alice");
        using var carol = await ClientFor("carol");
        await Befriend(carol, alice, "dev-carol");
        var roaster = await Put(alice, "vendors", new JsonObject { ["name"] = "Friedhats", ["kinds"] = 1 });
        var bean = await Put(alice, "beans", new JsonObject { ["name"] = "S1 Kenya", ["roasterId"] = roaster, ["visibility"] = VisibilityRules.Friends });

        var item = (await carol.GetFromJsonAsync<SharedItemDto>($"/friends/dev-alice/shared/beans/{bean}", Ct))!;

        item.Item["name"]!.GetValue<string>().ShouldBe("S1 Kenya");
        item.RoasterName.ShouldBe("Friedhats");
        item.OwnerName.ShouldBe("Alice");
        item.BeanName.ShouldBeNull();
    }

    [Theory]
    [InlineData(VisibilityRules.Private)]
    [InlineData(VisibilityRules.Household)]
    public async Task Items_not_shared_with_friends_are_not_found(int visibility)
    {
        using var alice = await ClientFor("alice");
        using var carol = await ClientFor("carol");
        await Befriend(carol, alice, "dev-carol");
        var bean = await Put(alice, "beans", new JsonObject { ["name"] = "S2 hidden", ["visibility"] = visibility });

        (await carol.GetAsync($"/friends/dev-alice/shared/beans/{bean}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_stranger_and_a_blocked_person_get_nothing()
    {
        using var alice = await ClientFor("alice");
        using var carol = await ClientFor("carol");
        var bean = await Put(alice, "beans", new JsonObject { ["name"] = "S3 friends", ["visibility"] = VisibilityRules.Friends });

        (await carol.GetAsync($"/friends/dev-alice/shared/beans/{bean}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await Befriend(carol, alice, "dev-carol");
        (await carol.GetAsync($"/friends/dev-alice/shared/beans/{bean}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await alice.PostAsync("/friends/blocks/dev-carol", null, Ct);
        (await carol.GetAsync($"/friends/dev-alice/shared/beans/{bean}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The store is shared by the tests of this class: leave carol as a stranger for the next one.
        await alice.DeleteAsync("/friends/blocks/dev-carol", Ct);
    }

    [Fact]
    public async Task A_recipe_comes_with_its_beans_name_only_when_the_bean_is_shared_too()
    {
        using var alice = await ClientFor("alice");
        using var carol = await ClientFor("carol");
        await Befriend(carol, alice, "dev-carol");
        var shared = await Put(alice, "beans", new JsonObject { ["name"] = "S4 shared bean", ["visibility"] = VisibilityRules.Friends });
        var hidden = await Put(alice, "beans", new JsonObject { ["name"] = "S4 household bean", ["visibility"] = VisibilityRules.Household });
        var withShared = await Put(alice, "recipes", new JsonObject { ["name"] = "S4 a", ["methodCode"] = "v60", ["beanId"] = shared, ["visibility"] = VisibilityRules.Friends });
        var withHidden = await Put(alice, "recipes", new JsonObject { ["name"] = "S4 b", ["methodCode"] = "v60", ["beanId"] = hidden, ["visibility"] = VisibilityRules.Friends });

        (await carol.GetFromJsonAsync<SharedItemDto>($"/friends/dev-alice/shared/recipes/{withShared}", Ct))!.BeanName.ShouldBe("S4 shared bean");
        (await carol.GetFromJsonAsync<SharedItemDto>($"/friends/dev-alice/shared/recipes/{withHidden}", Ct))!.BeanName.ShouldBeNull();
    }

    [Fact]
    public async Task Only_beans_and_recipes_can_be_fetched_and_deleted_ones_are_gone()
    {
        using var alice = await ClientFor("alice");
        using var carol = await ClientFor("carol");
        await Befriend(carol, alice, "dev-carol");
        var vendor = await Put(alice, "vendors", new JsonObject { ["name"] = "S5 vendor" });
        var bean = await Put(alice, "beans", new JsonObject { ["name"] = "S5 gone", ["visibility"] = VisibilityRules.Friends });
        var stored = (await alice.GetFromJsonAsync<SyncPageDto>("/sync/beans", Ct))!.Records.First(r => r.Entity["id"]!.GetValue<string>() == bean);
        var tombstone = (JsonObject)stored.Entity.DeepClone();
        tombstone["deleted"] = true;
        using var deleted = new HttpRequestMessage(HttpMethod.Put, $"/sync/beans/{bean}") { Content = JsonContent.Create(tombstone) };
        deleted.Headers.TryAddWithoutValidation("If-Match", stored.Version);
        (await alice.SendAsync(deleted, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await carol.GetAsync($"/friends/dev-alice/shared/vendors/{vendor}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await carol.GetAsync($"/friends/dev-alice/shared/beans/{bean}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await carol.GetAsync("/friends/dev-alice/shared/beans/does-not-exist", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
