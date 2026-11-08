using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dialed.Api.Auth;
using Dialed.Api.Households;
using Dialed.Api.Sync;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.Api.Tests;

public sealed class HouseholdTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A new person with a personal household, signed in.</summary>
    private sealed record Person(AppUser User, TokenResponse Tokens, HttpClient Client);

    private async Task<Person> NewPerson(string name, WebApplicationFactory<Program>? host = null)
    {
        host ??= factory;
        var id = Guid.NewGuid().ToString("N");
        var user = new AppUser(id, $"{id}@example.com", name, HouseholdService.PersonalHouseholdId(id));
        await host.Services.GetRequiredService<IUserDirectory>().AddAsync(user, Ct);
        var tokens = await host.Services.GetRequiredService<RefreshTokenService>().SignInAsync(user, Ct);
        return new Person(user, tokens, Authorized(host, tokens.AccessToken));
    }

    private static HttpClient Authorized(WebApplicationFactory<Program> host, string accessToken)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private static async Task<T> Body<T>(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<T>(Ct))!;

    private static async Task<string> Invite(Person owner)
    {
        using var response = await owner.Client.PostAsync("/households/mine/invites", null, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await Body<InviteDto>(response)).Code;
    }

    private static Task<HttpResponseMessage> Join(Person person, string code) =>
        person.Client.PostAsJsonAsync("/households/mine/join", new JoinRequest(code), Ct);

    /// <summary>The token for the person's household now, as the app gets it after joining or leaving.</summary>
    private async Task<TokenResponse> Refreshed(Person person)
    {
        using var anonymous = factory.CreateClient();
        using var response = await anonymous.PostAsJsonAsync("/auth/refresh", new RefreshRequest(person.Tokens.RefreshToken), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await Body<TokenResponse>(response);
    }

    [Fact]
    public async Task Everyone_starts_alone_in_a_household_they_own()
    {
        var thomas = await NewPerson("Thomas");

        var household = await Body<HouseholdDto>(await thomas.Client.GetAsync("/households/mine", Ct));

        household.Id.ShouldBe(thomas.User.HouseholdId);
        household.YouAreOwner.ShouldBeTrue();
        household.Members.Select(m => m.UserId).ShouldBe([thomas.User.Id]);
    }

    [Fact]
    public async Task Requires_a_signed_in_user()
    {
        using var anonymous = factory.CreateClient();
        (await anonymous.GetAsync("/households/mine", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_invite_brings_someone_into_the_household_and_their_new_token_shares_its_data()
    {
        var owner = await NewPerson("Owner");
        var guest = await NewPerson("Guest");
        var code = await Invite(owner);

        using var joined = await Join(guest, code.ToLowerInvariant()); // codes are typed by hand: case does not matter

        joined.StatusCode.ShouldBe(HttpStatusCode.OK);
        var household = await Body<HouseholdDto>(joined);
        household.Id.ShouldBe(owner.User.HouseholdId);
        household.Members.Select(m => m.DisplayName).ShouldBe(["Guest", "Owner"]);
        household.YouAreOwner.ShouldBeFalse();

        // The token carries the household, so the guest refreshes and then works on the owner's data.
        var refreshed = await Refreshed(guest);
        refreshed.HouseholdId.ShouldBe(owner.User.HouseholdId);
        using var guestClient = Authorized(factory, refreshed.AccessToken);
        var bean = new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["name"] = "From the guest", ["deleted"] = false };
        (await guestClient.PutAsJsonAsync($"/sync/beans/{bean["id"]}", bean, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var seen = await owner.Client.GetFromJsonAsync<SyncPageDto>("/sync/beans", Ct);
        seen!.Records.ShouldContain(r => r.Entity["name"]!.GetValue<string>() == "From the guest");
    }

    [Fact]
    public async Task An_invite_works_once()
    {
        var owner = await NewPerson("Owner");
        var code = await Invite(owner);
        (await Join(await NewPerson("First"), code)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await Join(await NewPerson("Second"), code)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unknown_codes_are_refused()
    {
        (await Join(await NewPerson("Guest"), "NOPE1234")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Invites_expire()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        await using var timed = factory.WithWebHostBuilder(b => b.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)));
        var owner = await NewPerson("Owner", timed);
        var guest = await NewPerson("Guest", timed);
        var code = await Invite(owner);

        clock.Now = clock.Now.AddDays(8);

        (await Join(guest, code)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Only_the_owner_invites()
    {
        var owner = await NewPerson("Owner");
        var guest = await NewPerson("Guest");
        (await Join(guest, await Invite(owner))).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var guestClient = Authorized(factory, (await Refreshed(guest)).AccessToken);

        using var response = await guestClient.PostAsync("/households/mine/invites", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Someone_in_a_shared_household_must_leave_before_joining_another()
    {
        var first = await NewPerson("First owner");
        var member = await NewPerson("Member");
        (await Join(member, await Invite(first))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var other = await NewPerson("Other owner");

        using var response = await Join(member, await Invite(other));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_member_can_leave_and_gets_a_household_of_their_own_but_the_owner_cannot_while_others_are_in()
    {
        var owner = await NewPerson("Owner");
        var member = await NewPerson("Member");
        (await Join(member, await Invite(owner))).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var ownerLeaves = await owner.Client.PostAsync("/households/mine/leave", null, Ct);
        ownerLeaves.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using var memberClient = Authorized(factory, (await Refreshed(member)).AccessToken);
        using var left = await memberClient.PostAsync("/households/mine/leave", null, Ct);
        left.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Body<HouseholdDto>(left)).Id.ShouldBe(member.User.HouseholdId);
        (await Refreshed(member)).HouseholdId.ShouldBe(member.User.HouseholdId);
    }

    [Fact]
    public async Task Leaving_when_alone_is_refused()
    {
        var alone = await NewPerson("Alone");
        using var response = await alone.Client.PostAsync("/households/mine/leave", null, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task The_owner_can_remove_a_member_who_gets_their_own_household_back()
    {
        var owner = await NewPerson("Owner");
        var member = await NewPerson("Member");
        (await Join(member, await Invite(owner))).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var removed = await owner.Client.DeleteAsync($"/households/mine/members/{member.User.Id}", Ct);

        removed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Body<HouseholdDto>(removed)).Members.Select(m => m.UserId).ShouldBe([owner.User.Id]);
        (await Refreshed(member)).HouseholdId.ShouldBe(member.User.HouseholdId);
    }

    [Fact]
    public async Task Members_cannot_remove_others_and_the_owner_cannot_remove_themselves()
    {
        var owner = await NewPerson("Owner");
        var member = await NewPerson("Member");
        (await Join(member, await Invite(owner))).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var memberClient = Authorized(factory, (await Refreshed(member)).AccessToken);

        (await memberClient.DeleteAsync($"/households/mine/members/{owner.User.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.Client.DeleteAsync($"/households/mine/members/{owner.User.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await owner.Client.DeleteAsync("/households/mine/members/nobody", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_development_households_exist_with_alice_as_owner()
    {
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/auth/dev", new DevLoginRequest("bob"), Ct);
        var tokens = await Body<TokenResponse>(login);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var household = await Body<HouseholdDto>(await client.GetAsync("/households/mine", Ct));

        household.Id.ShouldBe("dev-household");
        household.YouAreOwner.ShouldBeFalse();
        household.Members.Single(m => m.IsOwner).UserId.ShouldBe("dev-alice");
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
