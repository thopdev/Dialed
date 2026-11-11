using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Dialed.Api.Passkeys;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.Api.Tests;

/// <summary>Checks from the security review (#93): who can reach what, what the API tells browsers, and how recovery resists guessing.</summary>
public sealed class SecurityTests(PasskeyApp app) : IClassFixture<PasskeyApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Everything reachable without signing in, and why. A new endpoint that is not on this list must require authorization.</summary>
    private static readonly string[] AnonymousOnPurpose =
    [
        "GET /",
        "POST /auth/signup/options", // start of sign-up (invite code gate)
        "POST /auth/signup",
        "POST /auth/signin/options",
        "POST /auth/signin",
        "POST /auth/refresh", // the refresh token is the credential
        "POST /auth/signout",
        "POST /auth/recover/email", // always the same answer
        "POST /auth/recover/options", // needs a recovery or email code
        "POST /auth/recover",
        "POST /admin/invites", // needs the admin key header
        "PUT /photo-blobs/{householdId}/{file}", // local stand-in for Blob Storage: the signed URL is the credential, like a SAS
        "GET /photo-blobs/{householdId}/{file}",
    ];

    private static readonly string[] DevelopmentOnly = ["POST /auth/dev", "* /health", "* /alive"];

    [Fact]
    public void Every_endpoint_requires_a_signed_in_user_unless_it_is_listed_as_open_on_purpose()
    {
        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        var open = endpoints
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"]).Select(m => $"{m} {e.RoutePattern.RawText}"))
            .Order()
            .ToList();

        open.Where(o => !DevelopmentOnly.Contains(o)).ShouldBe(AnonymousOnPurpose.Order().ToList());
        endpoints.Count.ShouldBeGreaterThan(40); // the list above is not vacuous
    }

    [Fact]
    public async Task Answers_carry_security_headers_and_personal_data_is_not_cached()
    {
        await using var hsts = app.WithWebHostBuilder(b => b.UseSetting("Protection:Hsts", "true"));
        using var client = hsts.CreateClient();

        using var response = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken = "nope" }, Ct);

        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("default-src 'none'");
        response.Headers.GetValues("Strict-Transport-Security").Single().ShouldContain("max-age=31536000");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task Hsts_can_be_switched_off_for_local_development()
    {
        await using var local = app.WithWebHostBuilder(b => b.UseSetting("Protection:Hsts", "false"));
        using var client = local.CreateClient();

        using var response = await client.GetAsync("/", Ct);

        response.Headers.Contains("Strict-Transport-Security").ShouldBeFalse();
        response.Headers.Contains("X-Content-Type-Options").ShouldBeTrue();
    }

    private async Task<(string Email, IReadOnlyList<string> Codes)> SignUp()
    {
        var authenticator = new SoftwareAuthenticator();
        var email = $"sec-{Guid.NewGuid():N}@example.com";
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", PasskeyApp.AppOrigin);
        var start = (await (await client.PostAsJsonAsync("/auth/signup/options", new { email, displayName = "Sec" }, Ct)).Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonNode>(Ct))!;
        var credential = authenticator.Create(start["options"]!, PasskeyApp.AppOrigin);
        using var done = await client.PostAsJsonAsync("/auth/signup", new { state = start["state"]!.GetValue<string>(), credential }, Ct);
        var tokens = (await done.Content.ReadFromJsonAsync<Dialed.Api.Auth.TokenResponse>(Ct))!;
        return (email, tokens.RecoveryCodes!);
    }

    private async Task<HttpStatusCode> TryRecover(string email, string code)
    {
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", PasskeyApp.AppOrigin);
        using var response = await client.PostAsJsonAsync("/auth/recover/options", new { email, code }, Ct);
        return response.StatusCode;
    }

    [Fact]
    public async Task Too_many_wrong_recovery_codes_pause_recovery_even_for_the_right_code()
    {
        var (email, codes) = await SignUp();

        for (var i = 0; i < InMemoryEmailRecoveryStore.MaxFailures; i++)
        {
            (await TryRecover(email, "AAAA-AAAA")).ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await TryRecover(email, codes[0])).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_right_recovery_code_still_works_after_a_few_wrong_ones()
    {
        var (email, codes) = await SignUp();
        for (var i = 0; i < 3; i++)
        {
            (await TryRecover(email, "BBBB-BBBB")).ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await TryRecover(email, codes[0])).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Asking_again_for_an_email_code_within_a_minute_sends_nothing_more()
    {
        var (email, _) = await SignUp();
        var outbox = app.Services.GetRequiredService<OutboxEmailSender>();
        using var client = app.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            (await client.PostAsJsonAsync("/auth/recover/email", new { email }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        outbox.Sent.Count(m => m.To == email).ShouldBe(1);
        Regex.IsMatch(outbox.Sent.Single(m => m.To == email).Body, @"\d{8}").ShouldBeTrue();
    }
}
