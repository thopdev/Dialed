using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dialed.Api.Auth;
using Dialed.Api.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.Api.Tests;

/// <summary>Deleting an account (#35): needs a fresh passkey check and removes the person, their sessions and their data.</summary>
public sealed class DeleteAccountTests(PasskeyApp app) : IClassFixture<PasskeyApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Person(SoftwareAuthenticator Authenticator, TokenResponse Tokens, string Email);

    private static string NewEmail() => $"del-{Guid.NewGuid():N}@example.com";

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body, string? bearer)
    {
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Add("Origin", PasskeyApp.AppOrigin);
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return await client.SendAsync(request, Ct);
    }

    private async Task<JsonNode> Json(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonNode>(Ct))!;
    }

    private async Task<Person> SignUp(string name)
    {
        var authenticator = new SoftwareAuthenticator();
        var email = NewEmail();
        var start = await Json(await Send(HttpMethod.Post, "/auth/signup/options", new { email, displayName = name }, null));
        var credential = authenticator.Create(start["options"]!, PasskeyApp.AppOrigin);
        var tokens = (await (await Send(HttpMethod.Post, "/auth/signup", new { state = start["state"]!.GetValue<string>(), credential }, null)).Content.ReadFromJsonAsync<TokenResponse>(Ct))!;
        return new Person(authenticator, tokens, email);
    }

    private async Task<HttpResponseMessage> Delete(Person caller, SoftwareAuthenticator? otherAuthenticator = null)
    {
        var start = await Json(await Send(HttpMethod.Post, "/account/delete/options", new { }, caller.Tokens.AccessToken));
        var credential = (otherAuthenticator ?? caller.Authenticator).Get(start["options"]!, PasskeyApp.AppOrigin);
        return await Send(HttpMethod.Post, "/account/delete", new { state = start["state"]!.GetValue<string>(), credential }, caller.Tokens.AccessToken);
    }

    private async Task PushBean(Person person, string name)
    {
        var id = Guid.CreateVersion7().ToString();
        using var response = await Send(HttpMethod.Put, $"/sync/beans/{id}", new JsonObject { ["id"] = id, ["name"] = name, ["deleted"] = false, ["visibility"] = 1 }, person.Tokens.AccessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<TokenResponse> Refresh(Person person, string refreshToken)
    {
        using var response = await Send(HttpMethod.Post, "/auth/refresh", new { refreshToken }, null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(Ct))!;
    }

    [Fact]
    public async Task Deleting_needs_a_signed_in_user()
    {
        (await Send(HttpMethod.Post, "/account/delete/options", new { }, null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Post, "/account/delete", new { state = "x", credential = new { } }, null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Someone_elses_passkey_does_not_delete_the_account()
    {
        var victim = await SignUp("Victim");
        var attacker = await SignUp("Attacker");

        using var response = await Delete(victim, attacker.Authenticator);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Post, "/auth/refresh", new { refreshToken = victim.Tokens.RefreshToken }, null)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Deleting_removes_the_person_sessions_passkey_and_a_household_they_were_alone_in()
    {
        var person = await SignUp("Leaver");
        await PushBean(person, "Mine");
        var sync = app.Services.GetRequiredService<ISyncStore>();
        (await sync.ListAuthoredAsync(person.Tokens.HouseholdId, person.Tokens.UserId, "beans", Ct)).Count.ShouldBe(1);

        using var response = await Delete(person);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await sync.ListAuthoredAsync(person.Tokens.HouseholdId, person.Tokens.UserId, "beans", Ct)).ShouldBeEmpty();
        (await Send(HttpMethod.Post, "/auth/refresh", new { refreshToken = person.Tokens.RefreshToken }, null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var start = await Json(await Send(HttpMethod.Post, "/auth/signin/options", new { }, null));
        var credential = person.Authenticator.Get(start["options"]!, PasskeyApp.AppOrigin);
        (await Send(HttpMethod.Post, "/auth/signin", new { state = start["state"]!.GetValue<string>(), credential }, null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The email address can be used for a new account again.
        var again = await Send(HttpMethod.Post, "/auth/signup/options", new { email = person.Email, displayName = "Back" }, null);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_shared_household_keeps_its_items_without_the_person_and_passes_to_the_other_member()
    {
        var owner = await SignUp("Owner");
        var member = await SignUp("Member");
        await PushBean(owner, "Shared bean");
        var invite = await Json(await Send(HttpMethod.Post, "/households/mine/invites", null, owner.Tokens.AccessToken));
        (await Send(HttpMethod.Post, "/households/mine/join", new { code = invite["code"]!.GetValue<string>() }, member.Tokens.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var memberTokens = await Refresh(member, member.Tokens.RefreshToken);

        using var response = await Delete(owner);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var household = await Json(await Send(HttpMethod.Get, "/households/mine", null, memberTokens.AccessToken));
        household["youAreOwner"]!.GetValue<bool>().ShouldBeTrue();
        household["members"]!.AsArray().Count.ShouldBe(1);
        var page = await Json(await Send(HttpMethod.Get, "/sync/beans?pageSize=50", null, memberTokens.AccessToken));
        var bean = page["records"]!.AsArray().Select(r => r!["entity"]!).Single(e => e["name"]!.GetValue<string>() == "Shared bean");
        bean["createdBy"]!.GetValue<string>().ShouldBe("deleted-user");
        bean["updatedBy"]!.GetValue<string>().ShouldBe("deleted-user");
    }
}
