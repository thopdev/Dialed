using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dialed.Api.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.Api.Tests;

public sealed class AuthTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TokenResponse> DevLogin(HttpClient client, string user = "alice")
    {
        using var response = await client.PostAsJsonAsync("/auth/dev", new DevLoginRequest(user), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(Ct))!;
    }

    private static Task<HttpResponseMessage> Refresh(HttpClient client, string token) =>
        client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(token), Ct);

    [Fact]
    public async Task Dev_login_gives_tokens_that_open_protected_endpoints()
    {
        using var client = factory.CreateClient();
        var tokens = await DevLogin(client);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var me = await response.Content.ReadFromJsonAsync<AppUser>(Ct);
        me!.Id.ShouldBe("dev-alice");
        me.HouseholdId.ShouldBe("dev-household");
    }

    [Fact]
    public async Task Protected_endpoints_reject_missing_and_bad_tokens()
    {
        using var client = factory.CreateClient();
        (await client.GetAsync("/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
        (await client.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Dev_login_rejects_unknown_users()
    {
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/auth/dev", new DevLoginRequest("mallory"), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Dev_login_does_not_exist_outside_development()
    {
        using var production = factory.WithWebHostBuilder(builder => builder
            .UseEnvironment("Production")
            .UseSetting("Jwt:SigningKey", "a-production-key-that-is-long-enough-1234567890"));
        using var client = production.CreateClient();

        using var response = await client.PostAsJsonAsync("/auth/dev", new DevLoginRequest("alice"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public void Production_refuses_to_start_without_a_signing_key()
    {
        using var production = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        Should.Throw<Exception>(() => production.CreateClient());
    }

    [Fact]
    public async Task Refresh_hands_out_a_new_pair_and_retires_the_old_token()
    {
        using var client = factory.CreateClient();
        var first = await DevLogin(client, "bob");

        using var response = await Refresh(client, first.RefreshToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = (await response.Content.ReadFromJsonAsync<TokenResponse>(Ct))!;

        second.RefreshToken.ShouldNotBe(first.RefreshToken);
        second.UserId.ShouldBe("dev-bob");
        (await Refresh(client, second.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reusing_a_rotated_token_revokes_the_whole_sign_in()
    {
        using var client = factory.CreateClient();
        var first = await DevLogin(client);
        using var rotated = await Refresh(client, first.RefreshToken);
        var second = (await rotated.Content.ReadFromJsonAsync<TokenResponse>(Ct))!;

        (await Refresh(client, first.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Refresh(client, second.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Sign_out_stops_the_refresh_token_working()
    {
        using var client = factory.CreateClient();
        var tokens = await DevLogin(client);

        using var signOut = await client.PostAsJsonAsync("/auth/signout", new RefreshRequest(tokens.RefreshToken), Ct);
        signOut.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await Refresh(client, tokens.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unknown_refresh_tokens_are_rejected()
    {
        using var client = factory.CreateClient();
        (await Refresh(client, "nope")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Refresh(client, "")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_tokens_are_stored_hashed_only()
    {
        using var client = factory.CreateClient();
        var tokens = await DevLogin(client);

        var store = factory.Services.GetRequiredService<InMemoryRefreshTokenStore>();
        store.Hashes.ShouldNotContain(tokens.RefreshToken);
        store.Hashes.ShouldContain(RefreshTokenService.Hash(tokens.RefreshToken));
    }

    [Fact]
    public async Task Expired_refresh_tokens_are_rejected()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        using var timed = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<TimeProvider>(clock)));
        using var client = timed.CreateClient();
        var tokens = await DevLogin(client);

        clock.Now = clock.Now.AddDays(61);

        (await Refresh(client, tokens.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
