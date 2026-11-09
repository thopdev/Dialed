using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Dialed.Api.Auth;
using Dialed.Api.Passkeys;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.Api.Tests;

/// <summary>Recovery codes, email recovery and managing passkeys (#112).</summary>
public sealed class RecoveryTests(PasskeyApp app) : IClassFixture<PasskeyApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Person(SoftwareAuthenticator Authenticator, TokenResponse Tokens, string Email);

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

    private async Task<Person> SignUp()
    {
        var authenticator = new SoftwareAuthenticator();
        var email = $"rec-{Guid.NewGuid():N}@example.com";
        var start = await Json(await Send(HttpMethod.Post, "/auth/signup/options", new { email, displayName = "Rec" }, null));
        var credential = authenticator.Create(start["options"]!, PasskeyApp.AppOrigin);
        using var response = await Send(HttpMethod.Post, "/auth/signup", new { state = start["state"]!.GetValue<string>(), credential }, null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return new Person(authenticator, (await response.Content.ReadFromJsonAsync<TokenResponse>(Ct))!, email);
    }

    private async Task<HttpResponseMessage> Recover(string email, string code, SoftwareAuthenticator newDevice)
    {
        var start = await Send(HttpMethod.Post, "/auth/recover/options", new { email, code }, null);
        if (start.StatusCode != HttpStatusCode.OK)
        {
            return start;
        }

        var json = (await start.Content.ReadFromJsonAsync<JsonNode>(Ct))!;
        var credential = newDevice.Create(json["options"]!, PasskeyApp.AppOrigin);
        return await Send(HttpMethod.Post, "/auth/recover", new { state = json["state"]!.GetValue<string>(), credential }, null);
    }

    private async Task<HttpResponseMessage> SignInWith(SoftwareAuthenticator authenticator)
    {
        var start = await Json(await Send(HttpMethod.Post, "/auth/signin/options", new { }, null));
        var credential = authenticator.Get(start["options"]!, PasskeyApp.AppOrigin);
        return await Send(HttpMethod.Post, "/auth/signin", new { state = start["state"]!.GetValue<string>(), credential }, null);
    }

    [Fact]
    public async Task Sign_up_returns_recovery_codes_once()
    {
        var person = await SignUp();

        person.Tokens.RecoveryCodes.ShouldNotBeNull();
        person.Tokens.RecoveryCodes.Count.ShouldBe(RecoveryService.RecoveryCodeCount);
        person.Tokens.RecoveryCodes.Distinct().Count().ShouldBe(RecoveryService.RecoveryCodeCount);

        var status = await Json(await Send(HttpMethod.Get, "/account/recovery", null, person.Tokens.AccessToken));
        status["recoveryCodesLeft"]!.GetValue<int>().ShouldBe(RecoveryService.RecoveryCodeCount);

        // Signing in later does not show them again.
        var again = await (await SignInWith(person.Authenticator)).Content.ReadFromJsonAsync<TokenResponse>(Ct);
        again!.RecoveryCodes.ShouldBeNull();
    }

    [Fact]
    public async Task A_recovery_code_adds_a_new_passkey_and_ends_old_sessions_and_works_once()
    {
        var person = await SignUp();
        var code = person.Tokens.RecoveryCodes![0];
        var newDevice = new SoftwareAuthenticator();

        using var recovered = await Recover(person.Email, code.ToLowerInvariant(), newDevice);

        recovered.StatusCode.ShouldBe(HttpStatusCode.OK, await recovered.Content.ReadAsStringAsync(Ct));
        (await Send(HttpMethod.Post, "/auth/refresh", new { refreshToken = person.Tokens.RefreshToken }, null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SignInWith(newDevice)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SignInWith(person.Authenticator)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The code is used up.
        using var again = await Recover(person.Email, code, new SoftwareAuthenticator());
        again.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var status = await Json(await Send(HttpMethod.Get, "/account/recovery", null, (await recovered.Content.ReadFromJsonAsync<TokenResponse>(Ct))!.AccessToken));
        status["recoveryCodesLeft"]!.GetValue<int>().ShouldBe(RecoveryService.RecoveryCodeCount - 1);
    }

    [Fact]
    public async Task A_wrong_code_or_unknown_email_is_refused_the_same_way()
    {
        var person = await SignUp();

        using var wrong = await Recover(person.Email, "AAAA-AAAA", new SoftwareAuthenticator());
        using var unknown = await Recover($"nobody-{Guid.NewGuid():N}@example.com", person.Tokens.RecoveryCodes![0], new SoftwareAuthenticator());

        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unknown.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_email_code_recovers_the_account_and_unknown_addresses_get_the_same_answer()
    {
        var person = await SignUp();
        var outbox = app.Services.GetRequiredService<OutboxEmailSender>();

        (await Send(HttpMethod.Post, "/auth/recover/email", new { email = $"nobody-{Guid.NewGuid():N}@example.com" }, null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Send(HttpMethod.Post, "/auth/recover/email", new { email = person.Email }, null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var mail = outbox.Sent.Last(m => m.To == person.Email);
        var emailCode = Regex.Match(mail.Body, @"\d{8}").Value;
        using var wrong = await Recover(person.Email, emailCode == "00000000" ? "11111111" : "00000000", new SoftwareAuthenticator());
        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var newDevice = new SoftwareAuthenticator();
        using var recovered = await Recover(person.Email, emailCode, newDevice);
        recovered.StatusCode.ShouldBe(HttpStatusCode.OK, await recovered.Content.ReadAsStringAsync(Ct));
        (await SignInWith(newDevice)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var reused = await Recover(person.Email, emailCode, new SoftwareAuthenticator());
        reused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_email_code_stops_working_after_too_many_wrong_guesses()
    {
        var person = await SignUp();
        var outbox = app.Services.GetRequiredService<OutboxEmailSender>();
        await Send(HttpMethod.Post, "/auth/recover/email", new { email = person.Email }, null);
        var emailCode = Regex.Match(outbox.Sent.Last(m => m.To == person.Email).Body, @"\d{8}").Value;
        var wrongGuess = emailCode == "12345678" ? "87654321" : "12345678";

        for (var i = 0; i < InMemoryEmailRecoveryStore.MaxAttempts; i++)
        {
            using var attempt = await Send(HttpMethod.Post, "/auth/recover/options", new { email = person.Email, code = wrongGuess }, null);
            attempt.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var right = await Recover(person.Email, emailCode, new SoftwareAuthenticator());
        right.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Regenerating_replaces_all_recovery_codes()
    {
        var person = await SignUp();

        var fresh = (await Json(await Send(HttpMethod.Post, "/account/recovery/codes", null, person.Tokens.AccessToken)))["codes"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();

        fresh.Count.ShouldBe(RecoveryService.RecoveryCodeCount);
        using var old = await Recover(person.Email, person.Tokens.RecoveryCodes![0], new SoftwareAuthenticator());
        old.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var current = await Recover(person.Email, fresh[0], new SoftwareAuthenticator());
        current.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Another_passkey_can_be_added_renamed_and_removed_but_not_the_last_one()
    {
        var person = await SignUp();
        var token = person.Tokens.AccessToken;
        var second = new SoftwareAuthenticator();

        var start = await Json(await Send(HttpMethod.Post, "/account/passkeys/options", new { }, token));
        var credential = second.Create(start["options"]!, PasskeyApp.AppOrigin);
        using var added = await Send(HttpMethod.Post, "/account/passkeys?name=Work%20laptop", new { state = start["state"]!.GetValue<string>(), credential }, token);
        added.StatusCode.ShouldBe(HttpStatusCode.NoContent, await added.Content.ReadAsStringAsync(Ct));

        var list = (await Json(await Send(HttpMethod.Get, "/account/passkeys", null, token))).AsArray();
        list.Count.ShouldBe(2);
        var added2 = list.Single(p => p!["name"]!.GetValue<string>() == "Work laptop")!;

        (await Send(HttpMethod.Put, $"/account/passkeys/{added2["id"]!.GetValue<string>()}", new { name = "Office" }, token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Send(HttpMethod.Put, $"/account/passkeys/{added2["id"]!.GetValue<string>()}", new { name = "  " }, token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SignInWith(second)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await Send(HttpMethod.Delete, $"/account/passkeys/{added2["id"]!.GetValue<string>()}", null, token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var remaining = (await Json(await Send(HttpMethod.Get, "/account/passkeys", null, token))).AsArray();
        remaining.Count.ShouldBe(1);
        (await Send(HttpMethod.Delete, $"/account/passkeys/{remaining[0]!["id"]!.GetValue<string>()}", null, token)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Send(HttpMethod.Delete, "/account/passkeys/unknown", null, token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Managing_passkeys_needs_a_signed_in_user_and_only_touches_your_own()
    {
        (await Send(HttpMethod.Get, "/account/passkeys", null, null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Post, "/account/recovery/codes", null, null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var a = await SignUp();
        var b = await SignUp();
        var aKey = (await Json(await Send(HttpMethod.Get, "/account/passkeys", null, a.Tokens.AccessToken))).AsArray()[0]!["id"]!.GetValue<string>();
        (await Send(HttpMethod.Put, $"/account/passkeys/{aKey}", new { name = "Hijacked" }, b.Tokens.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Delete, $"/account/passkeys/{aKey}", null, b.Tokens.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
