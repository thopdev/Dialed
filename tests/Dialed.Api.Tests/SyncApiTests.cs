using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dialed.Api.Auth;
using Dialed.Api.Sync;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Dialed.Api.Tests;

public sealed class SyncApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
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

    private static JsonObject Bean(string id, string name, string? household = null)
    {
        var bean = new JsonObject { ["id"] = id, ["name"] = name, ["deleted"] = false };
        if (household is not null)
        {
            bean["householdId"] = household;
        }

        return bean;
    }

    private static async Task<HttpResponseMessage> Push(HttpClient client, string table, JsonObject entity, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/sync/{table}/{entity["id"]}") { Content = JsonContent.Create(entity) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<SyncPageDto> Pull(HttpClient client, string table, string query = "")
    {
        var page = await client.GetFromJsonAsync<SyncPageDto>($"/sync/{table}{query}", Ct);
        return page!;
    }

    private static string Id() => Guid.CreateVersion7().ToString();

    [Fact]
    public async Task Requires_a_signed_in_user()
    {
        using var anonymous = factory.CreateClient();
        (await anonymous.GetAsync("/sync/beans", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Pushed_records_are_stamped_by_the_server_and_can_be_pulled()
    {
        using var alice = await ClientFor("alice");
        var id = Id();

        using var response = await Push(alice, "beans", Bean(id, "Ethiopia"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = (await response.Content.ReadFromJsonAsync<SyncRecordDto>(Ct))!;
        stored.Version.ShouldBe("1");
        stored.Entity["householdId"]!.GetValue<string>().ShouldBe("dev-household");
        stored.Entity["createdBy"]!.GetValue<string>().ShouldBe("dev-alice");

        var page = await Pull(alice, "beans");
        page.Records.ShouldContain(r => r.Entity["id"]!.GetValue<string>() == id && r.Entity["name"]!.GetValue<string>() == "Ethiopia");
    }

    [Fact]
    public async Task Household_members_see_each_others_records_but_other_households_do_not()
    {
        using var alice = await ClientFor("alice");
        using var bob = await ClientFor("bob");
        using var carol = await ClientFor("carol");
        var id = Id();
        (await Push(alice, "beans", Bean(id, "Shared"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await Pull(bob, "beans")).Records.ShouldContain(r => r.Entity["id"]!.GetValue<string>() == id);
        (await Pull(carol, "beans")).Records.ShouldNotContain(r => r.Entity["id"]!.GetValue<string>() == id);
    }

    [Fact]
    public async Task Another_household_cannot_overwrite_or_even_see_a_record_by_id()
    {
        using var alice = await ClientFor("alice");
        using var carol = await ClientFor("carol");
        var id = Id();
        (await Push(alice, "beans", Bean(id, "Alice's"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Carol using the same id creates her own record in her own household; Alice's stays untouched.
        (await Push(carol, "beans", Bean(id, "Carol's"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var aliceRecord = (await Pull(alice, "beans")).Records.Single(r => r.Entity["id"]!.GetValue<string>() == id);
        aliceRecord.Entity["name"]!.GetValue<string>().ShouldBe("Alice's");
    }

    [Fact]
    public async Task Naming_another_household_in_the_record_is_forbidden()
    {
        using var carol = await ClientFor("carol");
        using var response = await Push(carol, "beans", Bean(Id(), "Sneaky", household: "dev-household"));
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Updates_need_the_current_version()
    {
        using var alice = await ClientFor("alice");
        var id = Id();
        await Push(alice, "beans", Bean(id, "v1"));

        using var ok = await Push(alice, "beans", Bean(id, "v2"), ifMatch: "1");
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ok.Content.ReadFromJsonAsync<SyncRecordDto>(Ct))!.Version.ShouldBe("2");

        using var stale = await Push(alice, "beans", Bean(id, "stale"), ifMatch: "1");
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var server = (await stale.Content.ReadFromJsonAsync<SyncRecordDto>(Ct))!;
        server.Version.ShouldBe("2");
        server.Entity["name"]!.GetValue<string>().ShouldBe("v2");

        using var missing = await Push(alice, "beans", Bean(id, "no header"));
        missing.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Creating_with_a_version_for_an_unknown_record_is_refused()
    {
        using var alice = await ClientFor("alice");
        using var response = await Push(alice, "beans", Bean(Id(), "ghost"), ifMatch: "3");
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Deleted_records_are_still_pulled_so_other_devices_learn_of_the_delete()
    {
        using var alice = await ClientFor("alice");
        var id = Id();
        await Push(alice, "bags", Bean(id, "bag"));
        var deleted = Bean(id, "bag");
        deleted["deleted"] = true;
        await Push(alice, "bags", deleted, ifMatch: "1");

        var record = (await Pull(alice, "bags")).Records.Single(r => r.Entity["id"]!.GetValue<string>() == id);
        record.Entity["deleted"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task Pulls_page_through_changes_by_watermark()
    {
        using var carol = await ClientFor("carol");
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var id = Id();
            ids.Add(id);
            await Push(carol, "equipment", Bean(id, $"item {i}"));
        }

        var seen = new List<string>();
        string query = "?pageSize=2";
        while (true)
        {
            var page = await Pull(carol, "equipment", query);
            seen.AddRange(page.Records.Select(r => r.Entity["id"]!.GetValue<string>()));
            if (!page.HasMore)
            {
                break;
            }

            var last = page.Records[^1].Entity;
            query = $"?pageSize=2&afterUpdatedAt={Uri.EscapeDataString(last["updatedAt"]!.GetValue<DateTimeOffset>().ToString("O"))}&afterId={last["id"]!.GetValue<string>()}";
        }

        seen.ShouldBe(ids, ignoreOrder: true);
        seen.Count.ShouldBe(seen.Distinct().Count());
    }

    [Fact]
    public async Task Unknown_tables_are_not_found()
    {
        using var alice = await ClientFor("alice");
        (await alice.GetAsync("/sync/secrets", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Push(alice, "secrets", Bean(Id(), "x"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_record_id_must_match_the_url()
    {
        using var alice = await ClientFor("alice");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/sync/beans/{Id()}") { Content = JsonContent.Create(Bean(Id(), "mismatch")) };
        (await alice.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
