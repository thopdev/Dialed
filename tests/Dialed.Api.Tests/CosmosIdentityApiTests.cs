using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dialed.Api.Auth;
using Dialed.Api.Passkeys;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.Api.Tests;

/// <summary>
/// The whole sign-up, refresh and sign-in flow on Cosmos DB, across a restart (#38): the API that signs someone in is not the
/// one that created the account. Skips without <c>COSMOS_CONNECTION_STRING</c>.
/// </summary>
public sealed class CosmosIdentityApiTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly CosmosTestDatabase database = new();
    private readonly List<WebApplicationFactory<Program>> apps = [];
    private readonly SoftwareAuthenticator authenticator = new();

    private WebApplicationFactory<Program> NewApp()
    {
        CosmosTestDatabase.SkipWithoutEmulator();
        var app = new CosmosApp($"{CosmosTestDatabase.ConnectionString!.TrimEnd(';')};Database={database.Name}");
        apps.Add(app);
        return app;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in apps)
        {
            await app.DisposeAsync();
        }

        if (apps.Count > 0)
        {
            database.New(); // a client to delete the database with
        }

        await database.DisposeAsync();
    }

    [Fact]
    public async Task An_account_made_before_a_restart_refreshes_and_signs_in_after_it()
    {
        var first = NewApp();
        first.Services.GetRequiredService<IRefreshTokenStore>().ShouldBeOfType<CosmosRefreshTokenStore>();
        var email = $"cosmos-{Guid.NewGuid():N}@example.com";
        var (options, state) = await Options(first, "/auth/signup/options", new { email, displayName = "Restarted" });
        var signedUp = await Complete(first, "/auth/signup", new { state, credential = authenticator.Create(options, PasskeyApp.AppOrigin) });

        var second = NewApp();
        using var client = second.CreateClient();
        using var refreshed = await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(signedUp.RefreshToken), Ct);
        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await refreshed.Content.ReadFromJsonAsync<TokenResponse>(Ct))!.UserId.ShouldBe(signedUp.UserId);
        using var replayed = await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(signedUp.RefreshToken), Ct);
        replayed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var (signInOptions, signInState) = await Options(second, "/auth/signin/options", new { });
        var signedIn = await Complete(second, "/auth/signin", new { state = signInState, credential = authenticator.Get(signInOptions, PasskeyApp.AppOrigin) });
        signedIn.UserId.ShouldBe(signedUp.UserId);
        signedIn.HouseholdId.ShouldBe(signedUp.HouseholdId);

        // The email stays taken on the new instance.
        using var taken = await Post(second, "/auth/signup/options", new { email, displayName = "Copycat" });
        taken.StatusCode.ShouldNotBe(HttpStatusCode.OK);
    }

    private static async Task<(JsonNode Options, string State)> Options(WebApplicationFactory<Program> app, string path, object body)
    {
        using var response = await Post(app, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var json = (await response.Content.ReadFromJsonAsync<JsonNode>(Ct))!;
        return (json["options"]!, json["state"]!.GetValue<string>());
    }

    private static async Task<TokenResponse> Complete(WebApplicationFactory<Program> app, string path, object body)
    {
        using var response = await Post(app, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(Ct))!;
    }

    private static async Task<HttpResponseMessage> Post(WebApplicationFactory<Program> app, string path, object body)
    {
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Origin", PasskeyApp.AppOrigin);
        return await client.SendAsync(request, Ct);
    }

    private sealed class CosmosApp(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("App:Origins:0", PasskeyApp.AppOrigin);
            builder.UseSetting("Passkeys:CeremonyLifetime", "00:05:00");
            builder.UseSetting("ConnectionStrings:dialed", connectionString);
        }
    }
}
