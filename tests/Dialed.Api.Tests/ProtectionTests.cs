using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Dialed.Api.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Dialed.Api.Tests;

public sealed class ProtectionTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> With(params (string Key, string Value)[] settings) =>
        factory.WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

    private static async Task<HttpClient> SignedIn(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        using var login = await client.PostAsJsonAsync("/auth/dev", new DevLoginRequest("alice"), Ct);
        var tokens = (await login.Content.ReadFromJsonAsync<TokenResponse>(Ct))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    [Fact]
    public async Task Auth_endpoints_are_limited_per_address_with_a_retry_hint()
    {
        await using var app = With(("Protection:AuthRequestsPerMinute", "3"));
        using var client = app.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            using var ok = await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest("unknown"), Ct);
            ok.StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // not limited yet: the token is simply wrong
        }

        using var limited = await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest("unknown"), Ct);

        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull();
        (await limited.Content.ReadAsStringAsync(Ct)).ShouldContain("Too many requests");
    }

    [Fact]
    public async Task Ordinary_endpoints_are_limited_per_user_and_other_users_are_unaffected()
    {
        await using var app = With(("Protection:RequestsPerMinute", "3"));
        using var alice = await SignedIn(app);
        for (var i = 0; i < 3; i++)
        {
            (await alice.GetAsync("/sync/beans", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await alice.GetAsync("/sync/beans", Ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        using var bob = app.CreateClient();
        using var login = await bob.PostAsJsonAsync("/auth/dev", new DevLoginRequest("bob"), Ct);
        var tokens = (await login.Content.ReadFromJsonAsync<TokenResponse>(Ct))!;
        bob.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        (await bob.GetAsync("/sync/beans", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_checks_are_never_limited()
    {
        await using var app = With(("Protection:RequestsPerMinute", "1"));
        using var client = app.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            (await client.GetAsync("/health", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Oversized_bodies_are_refused_before_they_are_read()
    {
        await using var app = With(("Protection:MaxRequestBodyBytes", "200"));
        using var client = await SignedIn(app);
        var big = new JsonObject { ["id"] = "b1", ["name"] = new string('x', 500), ["deleted"] = false };

        using var response = await client.PutAsJsonAsync("/sync/beans/b1", big, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Request too large");
    }

    [Theory]
    [InlineData("bad id!", "b1", "id")]
    [InlineData("b1", "b2", "id")]
    public async Task Pushed_records_need_a_sane_id_that_matches_the_address(string routeId, string bodyId, string field)
    {
        using var client = await SignedIn(factory);
        var entity = new JsonObject { ["id"] = bodyId, ["name"] = "Gesha", ["deleted"] = false };

        using var response = await client.PutAsJsonAsync($"/sync/beans/{Uri.EscapeDataString(routeId)}", entity, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain(field);
    }

    [Fact]
    public async Task Pushed_records_with_huge_text_or_lists_are_refused_with_problem_details()
    {
        using var client = await SignedIn(factory);
        var longText = new JsonObject { ["id"] = "b1", ["name"] = new string('x', 4001), ["deleted"] = false };
        var longList = new JsonObject { ["id"] = "b2", ["name"] = "ok", ["deleted"] = false, ["tags"] = new JsonArray(Enumerable.Range(0, 501).Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()) };

        using var text = await client.PutAsJsonAsync("/sync/beans/b1", longText, Ct);
        using var list = await client.PutAsJsonAsync("/sync/beans/b2", longList, Ct);

        text.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await text.Content.ReadAsStringAsync(Ct)).ShouldContain("At most 4000 characters");
        list.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await list.Content.ReadAsStringAsync(Ct)).ShouldContain("At most 500 items");
        text.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Normal_records_still_sync()
    {
        using var client = await SignedIn(factory);
        var entity = new JsonObject { ["id"] = Guid.CreateVersion7().ToString(), ["name"] = "Gesha", ["deleted"] = false };

        using var response = await client.PutAsync($"/sync/beans/{entity["id"]}", new StringContent(entity.ToJsonString(), Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
