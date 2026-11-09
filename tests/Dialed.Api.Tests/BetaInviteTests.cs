using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dialed.Api.Passkeys;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Dialed.Api.Tests;

public sealed class GatedApp : WebApplicationFactory<Program>
{
    public const string AdminKey = "test-admin-key";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("App:Origins:0", PasskeyApp.AppOrigin);
        builder.UseSetting("Beta:InviteRequired", "true");
        builder.UseSetting("Beta:AdminKey", AdminKey);
    }
}

/// <summary>Invite-only sign-up (#113): the gate, the admin endpoint and the feature flag.</summary>
public sealed class BetaInviteTests(GatedApp app, PasskeyApp open) : IClassFixture<GatedApp>, IClassFixture<PasskeyApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewEmail() => $"beta-{Guid.NewGuid():N}@example.com";

    private async Task<HttpResponseMessage> SignUpOptions(WebApplicationFactory<Program> factory, string? inviteCode)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/signup/options")
        {
            Content = JsonContent.Create(new { email = NewEmail(), displayName = "Beta", inviteCode }),
        };
        request.Headers.Add("Origin", PasskeyApp.AppOrigin);
        return await client.SendAsync(request, Ct);
    }

    private async Task<(JsonNode Options, string State)> SignUpStart(string code)
    {
        using var response = await SignUpOptions(app, code);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var json = (await response.Content.ReadFromJsonAsync<JsonNode>(Ct))!;
        return (json["options"]!, json["state"]!.GetValue<string>());
    }

    private async Task<HttpResponseMessage> Complete(string state, JsonNode credential)
    {
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/signup") { Content = JsonContent.Create(new { state, credential }) };
        request.Headers.Add("Origin", PasskeyApp.AppOrigin);
        return await client.SendAsync(request, Ct);
    }

    private async Task<string> CreateInvite(object body, string key = GatedApp.AdminKey)
    {
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/invites") { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Admin-Key", key);
        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonNode>(Ct))!["code"]!.GetValue<string>();
    }

    [Fact]
    public async Task Sign_up_without_a_code_or_with_an_unknown_code_is_refused()
    {
        (await SignUpOptions(app, null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SignUpOptions(app, "")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SignUpOptions(app, "NOSUCHCODE")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_valid_code_allows_sign_up_and_a_single_use_code_is_then_used_up()
    {
        var code = await CreateInvite(new { maxUses = 1 });
        var (options, state) = await SignUpStart(code);

        using var done = await Complete(state, new SoftwareAuthenticator().Create(options, PasskeyApp.AppOrigin));

        done.StatusCode.ShouldBe(HttpStatusCode.OK, await done.Content.ReadAsStringAsync(Ct));
        (await SignUpOptions(app, code)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_last_use_cannot_be_taken_by_two_sign_ups_that_started_together()
    {
        var code = await CreateInvite(new { maxUses = 1 });
        var first = await SignUpStart(code);
        var second = await SignUpStart(code);

        using var one = await Complete(first.State, new SoftwareAuthenticator().Create(first.Options, PasskeyApp.AppOrigin));
        using var two = await Complete(second.State, new SoftwareAuthenticator().Create(second.Options, PasskeyApp.AppOrigin));

        one.StatusCode.ShouldBe(HttpStatusCode.OK);
        two.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_code_with_several_uses_works_that_many_times()
    {
        var code = await CreateInvite(new { maxUses = 2 });
        for (var i = 0; i < 2; i++)
        {
            var (options, state) = await SignUpStart(code);
            using var done = await Complete(state, new SoftwareAuthenticator().Create(options, PasskeyApp.AppOrigin));
            done.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await SignUpOptions(app, code)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Codes_are_not_case_sensitive_and_can_be_chosen_by_the_admin()
    {
        var code = await CreateInvite(new { code = "Friends2026", maxUses = 5 });

        code.ShouldBe("FRIENDS2026");
        (await SignUpOptions(app, "friends2026")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_admin_endpoint_needs_the_admin_key()
    {
        using var client = app.CreateClient();
        using var wrong = new HttpRequestMessage(HttpMethod.Post, "/admin/invites") { Content = JsonContent.Create(new { maxUses = 1 }) };
        wrong.Headers.Add("X-Admin-Key", "nope");
        using var none = new HttpRequestMessage(HttpMethod.Post, "/admin/invites") { Content = JsonContent.Create(new { maxUses = 1 }) };

        (await client.SendAsync(wrong, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.SendAsync(none, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Without_an_admin_key_configured_the_endpoint_does_not_exist()
    {
        using var client = open.CreateClient();

        (await client.PostAsJsonAsync("/admin/invites", new { maxUses = 1 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task With_the_flag_off_sign_up_needs_no_code()
    {
        (await SignUpOptions(open, null)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_expired_code_is_not_valid()
    {
        var store = new InMemoryBetaInviteStore();
        var now = DateTimeOffset.UtcNow;
        await store.AddAsync(new BetaInvite("OLD", 1, 0, now.AddDays(-1)), Ct);
        await store.AddAsync(new BetaInvite("NEW", 1, 0, now.AddDays(1)), Ct);

        (await store.IsValidAsync("OLD", now, Ct)).ShouldBeFalse();
        (await store.TryUseAsync("OLD", now, Ct)).ShouldBeFalse();
        (await store.IsValidAsync("NEW", now, Ct)).ShouldBeTrue();
    }
}
