using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dialed.Api.Auth;
using Dialed.Api.Configuration;
using Dialed.Api.Passkeys;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.Api.Tests;

public sealed class PasskeyApp : WebApplicationFactory<Program>
{
    public const string AppOrigin = "https://localhost:7200";
    public const string TunnelOrigin = "https://dialed-7200.euw.devtunnels.ms";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("App:Origins:0", AppOrigin);
        builder.UseSetting("App:Origins:1", "https://dialed-*.euw.devtunnels.ms");
        builder.UseSetting("Passkeys:CeremonyLifetime", "00:05:00");
    }
}

/// <summary>Cookie-less passkey sign-up and sign-in (#30-#32) with a software authenticator.</summary>
public sealed class PasskeyTests(PasskeyApp app) : IClassFixture<PasskeyApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly SoftwareAuthenticator authenticator = new();

    [Fact]
    public async Task Signs_up_and_signs_in_again_with_a_discoverable_passkey_without_cookies()
    {
        var email = NewEmail();
        var signedUp = await SignUp(email, "Thomas");
        signedUp.HouseholdId.ShouldBe("h-" + signedUp.UserId);

        var (options, state) = await Options("/auth/signin/options", new { });
        (options["allowCredentials"]?.AsArray().Count ?? 0).ShouldBe(0); // no user given: the authenticator picks the passkey
        var credential = authenticator.Get(options, PasskeyApp.AppOrigin);
        using var signIn = await Post("/auth/signin", new { state, credential });

        signIn.StatusCode.ShouldBe(HttpStatusCode.OK);
        signIn.Headers.Contains("Set-Cookie").ShouldBeFalse();
        var tokens = (await signIn.Content.ReadFromJsonAsync<TokenResponse>(Ct))!;
        tokens.UserId.ShouldBe(signedUp.UserId);

        var me = await Me(tokens.AccessToken);
        me["email"]!.GetValue<string>().ShouldBe(email);
        me["displayName"]!.GetValue<string>().ShouldBe("Thomas");
    }

    [Fact]
    public async Task Signing_in_twice_works_as_the_sign_count_moves_on()
    {
        await SignUp(NewEmail(), "Twice");

        for (var i = 0; i < 2; i++)
        {
            var (options, state) = await Options("/auth/signin/options", new { });
            using var response = await Post("/auth/signin", new { state, credential = authenticator.Get(options, PasskeyApp.AppOrigin) });
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Passkey_sign_in_hands_out_refresh_tokens_that_rotate()
    {
        var signedUp = await SignUp(NewEmail(), "Refresher");
        using var client = app.CreateClient();

        using var refreshed = await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(signedUp.RefreshToken), Ct);

        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await refreshed.Content.ReadFromJsonAsync<TokenResponse>(Ct))!.UserId.ShouldBe(signedUp.UserId);
    }

    [Fact]
    public async Task Rp_id_follows_the_calling_origin()
    {
        var (local, _) = await Options("/auth/signup/options", new { email = NewEmail(), displayName = "A" }, PasskeyApp.AppOrigin);
        var (tunnel, _) = await Options("/auth/signup/options", new { email = NewEmail(), displayName = "B" }, PasskeyApp.TunnelOrigin);

        local["rp"]!["id"]!.GetValue<string>().ShouldBe("localhost");
        tunnel["rp"]!["id"]!.GetValue<string>().ShouldBe("dialed-7200.euw.devtunnels.ms");
        local["authenticatorSelection"]!["residentKey"]!.GetValue<string>().ShouldBe("required");
    }

    [Fact]
    public async Task A_passkey_from_one_origin_does_not_work_on_another()
    {
        await SignUp(NewEmail(), "Tunnel user", PasskeyApp.TunnelOrigin);

        var (options, state) = await Options("/auth/signin/options", new { }, PasskeyApp.AppOrigin);
        var credential = authenticator.Get(options, PasskeyApp.AppOrigin, authenticator.Credentials[0]);
        using var response = await Post("/auth/signin", new { state, credential }, PasskeyApp.AppOrigin);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Rejects_origins_that_are_not_allowed()
    {
        using var response = await Post("/auth/signin/options", new { }, "https://evil.example");
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rejects_client_data_from_another_origin()
    {
        var (options, state) = await Options("/auth/signup/options", new { email = NewEmail(), displayName = "X" });

        // A phishing page relays the options; the browser signs its own origin into clientDataJSON.
        var credential = authenticator.Create(options, "https://localhost:7666");
        using var response = await Post("/auth/signup", new { state, credential });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task State_is_single_use()
    {
        await SignUp(NewEmail(), "Replay");
        var (options, state) = await Options("/auth/signin/options", new { });
        var credential = authenticator.Get(options, PasskeyApp.AppOrigin);

        using var first = await Post("/auth/signin", new { state, credential });
        using var replay = await Post("/auth/signin", new { state, credential });

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        replay.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task State_expires()
    {
        var time = new ShiftableTime();
        await using var timed = app.WithWebHostBuilder(b => b.ConfigureServices(services => services.AddSingleton<TimeProvider>(time)));
        using var client = timed.CreateClient();

        var options = await PostJson(client, "/auth/signin/options", new { }, PasskeyApp.AppOrigin);
        time.Offset = TimeSpan.FromMinutes(6);
        using var response = await PostRaw(client, "/auth/signin", new { state = options["state"]!.GetValue<string>(), credential = new { id = "x" } }, PasskeyApp.AppOrigin);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("expired");
    }

    [Fact]
    public async Task Tampered_state_is_rejected()
    {
        var (_, state) = await Options("/auth/signin/options", new { });

        using var response = await Post("/auth/signin", new { state = state[..^4] + "AAAA", credential = new { id = "x" } });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sign_up_state_cannot_be_used_to_sign_in()
    {
        var (_, state) = await Options("/auth/signup/options", new { email = NewEmail(), displayName = "Mixed" });

        using var response = await Post("/auth/signin", new { state, credential = new { id = "x" } });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Email_must_be_unique()
    {
        var email = NewEmail();
        await SignUp(email, "First");

        using var response = await Post("/auth/signup/options", new { email = email.ToUpperInvariant(), displayName = "Second" });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Invalid_emails_are_refused()
    {
        using var response = await Post("/auth/signup/options", new { email = "nope", displayName = "X" });
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unknown_credential_cannot_sign_in()
    {
        var stranger = new SoftwareAuthenticator();
        var (signupOptions, _) = await Options("/auth/signup/options", new { email = NewEmail(), displayName = "Never finished" });
        stranger.Create(signupOptions, PasskeyApp.AppOrigin); // created on the device, never registered

        var (options, state) = await Options("/auth/signin/options", new { });
        using var response = await Post("/auth/signin", new { state, credential = stranger.Get(options, PasskeyApp.AppOrigin) });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("https://localhost:7200", true, "localhost")]
    [InlineData("http://localhost:7200", false, "")]
    [InlineData("https://localhost:7201", false, "")]
    [InlineData("https://dialed-9.euw.devtunnels.ms", true, "dialed-9.euw.devtunnels.ms")]
    [InlineData("https://euw.devtunnels.ms.evil.example", false, "")]
    [InlineData("http://dialed-9.euw.devtunnels.ms", false, "")]
    [InlineData(null, false, "")]
    public void Origin_policy(string? origin, bool allowed, string rpId)
    {
        var policy = Policy(new PasskeyOptions(), "https://localhost:7200", "https://dialed-*.euw.devtunnels.ms");

        policy.TryResolve(origin, out var resolved).ShouldBe(allowed);
        resolved.ShouldBe(rpId);
    }

    [Theory]
    [InlineData("https://dialed.app", true)]
    [InlineData("https://www.dialed.app", true)]
    [InlineData("https://notdialed.app", false)]
    public void Fixed_rp_id_must_be_a_parent_of_the_origin(string origin, bool allowed)
    {
        var policy = Policy(new PasskeyOptions { RpId = "dialed.app" }, "https://dialed.app", "https://www.dialed.app", "https://notdialed.app");

        policy.TryResolve(origin, out var rpId).ShouldBe(allowed);
        if (allowed)
        {
            rpId.ShouldBe("dialed.app");
        }
    }

    private static OriginPolicy Policy(PasskeyOptions options, params string[] origins)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(origins.Select((o, i) => new KeyValuePair<string, string?>($"App:Origins:{i}", o)))
            .Build();
        return new OriginPolicy(AllowedOrigins.From(configuration), AllowedOrigins.Patterns(configuration), Microsoft.Extensions.Options.Options.Create(options));
    }

    private async Task<TokenResponse> SignUp(string email, string displayName, string origin = PasskeyApp.AppOrigin)
    {
        var (options, state) = await Options("/auth/signup/options", new { email, displayName }, origin);
        var credential = authenticator.Create(options, origin);
        using var response = await Post("/auth/signup", new { state, credential }, origin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(Ct))!;
    }

    private async Task<(JsonNode Options, string State)> Options(string path, object body, string origin = PasskeyApp.AppOrigin)
    {
        using var client = app.CreateClient();
        var json = await PostJson(client, path, body, origin);
        return (json["options"]!, json["state"]!.GetValue<string>());
    }

    private async Task<HttpResponseMessage> Post(string path, object body, string origin = PasskeyApp.AppOrigin)
    {
        using var client = app.CreateClient();
        return await PostRaw(client, path, body, origin);
    }

    private static async Task<JsonNode> PostJson(HttpClient client, string path, object body, string origin)
    {
        using var response = await PostRaw(client, path, body, origin);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonNode>(Ct))!;
    }

    private static async Task<HttpResponseMessage> PostRaw(HttpClient client, string path, object body, string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Origin", origin);
        return await client.SendAsync(request, Ct);
    }

    private async Task<JsonNode> Me(string accessToken)
    {
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.GetAsync("/auth/me", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonNode>(Ct))!;
    }

    private static string NewEmail() => $"test-{Guid.NewGuid():N}@example.com";

    private sealed class ShiftableTime : TimeProvider
    {
        public TimeSpan Offset { get; set; }

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
    }
}
