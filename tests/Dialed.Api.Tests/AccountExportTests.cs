using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dialed.Api.Auth;
using Dialed.Api.Sync;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Dialed.Api.Tests;

public sealed class AccountExportTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
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

    private static async Task Push(HttpClient client, string table, string name, int visibility, bool deleted = false)
    {
        var id = Guid.CreateVersion7().ToString();
        using var response = await client.PutAsJsonAsync($"/sync/{table}/{id}", new JsonObject { ["id"] = id, ["name"] = name, ["deleted"] = deleted, ["visibility"] = visibility }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_export_holds_the_profile_and_everything_the_person_made_including_private_items()
    {
        using var alice = await ClientFor("alice");
        using var bob = await ClientFor("bob");
        await Push(alice, "beans", "Export private", VisibilityRules.Private);
        await Push(alice, "beans", "Export shared", VisibilityRules.Friends);
        await Push(alice, "vendors", "Export vendor", VisibilityRules.Household);
        await Push(alice, "beans", "Export removed", VisibilityRules.Household, deleted: true);
        await Push(bob, "beans", "Export bobs", VisibilityRules.Household);

        using var response = await alice.GetAsync("/account/export", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentDisposition!.FileName.ShouldBe("dialed-export.json");
        var export = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        export["formatVersion"]!.GetValue<int>().ShouldBe(1);
        export["profile"]!["displayName"]!.GetValue<string>().ShouldBe("Alice");
        var beans = export["items"]!["beans"]!.AsArray().Select(b => b!["name"]!.GetValue<string>()).Where(n => n.StartsWith("Export", StringComparison.Ordinal)).Order().ToArray();
        beans.ShouldBe(["Export private", "Export shared"]);
        export["items"]!["vendors"]!.AsArray().Select(v => v!["name"]!.GetValue<string>()).ShouldContain("Export vendor");
        export["items"]!.AsObject().ContainsKey("photos").ShouldBeTrue();
    }

    [Fact]
    public async Task The_export_needs_a_signed_in_user()
    {
        using var anonymous = factory.CreateClient();

        (await anonymous.GetAsync("/account/export", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
