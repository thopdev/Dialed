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
    public async Task The_owner_can_transfer_ownership_and_then_leave()
    {
        var owner = await NewPerson("Owner");
        var member = await NewPerson("Member");
        (await Join(member, await Invite(owner))).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var transferred = await owner.Client.PostAsJsonAsync("/households/mine/transfer", new TransferRequest(member.User.Id), Ct);

        transferred.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Body<HouseholdDto>(transferred)).YouAreOwner.ShouldBeFalse();
        var asMember = await Body<HouseholdDto>(await member.Client.GetAsync("/households/mine", Ct));
        asMember.YouAreOwner.ShouldBeTrue();
        asMember.Members.Single(m => m.IsOwner).UserId.ShouldBe(member.User.Id);

        // The former owner is an ordinary member now: no owner actions. It is their only household, so leaving it needs another one first.
        (await owner.Client.PostAsync("/households/mine/invites", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.Client.PostAsync("/households/mine/leave", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var elsewhere = await NewPerson("Elsewhere");
        (await Join(owner, await Invite(elsewhere))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.Client.PostAsJsonAsync("/households/mine/switch", new SwitchRequest(owner.User.HouseholdId), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.Client.PostAsync("/households/mine/leave", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Transferring_needs_the_owner_and_a_member_of_the_household()
    {
        var owner = await NewPerson("Owner");
        var member = await NewPerson("Member");
        var outsider = await NewPerson("Outsider");
        (await Join(member, await Invite(owner))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await member.Client.PostAsJsonAsync("/households/mine/transfer", new TransferRequest(member.User.Id), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.Client.PostAsJsonAsync("/households/mine/transfer", new TransferRequest(outsider.User.Id), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await owner.Client.PostAsJsonAsync("/households/mine/transfer", new TransferRequest(owner.User.Id), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Body<HouseholdDto>(await owner.Client.GetAsync("/households/mine", Ct))).YouAreOwner.ShouldBeTrue();
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

    private static async Task<IReadOnlyList<HouseholdSummaryDto>> Households(Person person) =>
        (await person.Client.GetFromJsonAsync<List<HouseholdSummaryDto>>("/households", Ct))!;

    /// <summary>A client with a fresh token for the household the person is in right now (the app gets it by refreshing; a new sign-in is the same for a test).</summary>
    private async Task<HttpClient> ClientFor(Person person) => Authorized(factory, (await NowSignedIn(person)).AccessToken);

    private async Task<TokenResponse> NowSignedIn(Person person)
    {
        var user = (await factory.Services.GetRequiredService<IUserDirectory>().FindByIdAsync(person.User.Id, Ct))!;
        return await factory.Services.GetRequiredService<RefreshTokenService>().SignInAsync(user, Ct);
    }

    private static async Task PutBean(HttpClient client, string name)
    {
        var bean = new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["name"] = name, ["deleted"] = false };
        (await client.PutAsJsonAsync($"/sync/beans/{bean["id"]}", bean, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<List<string>> BeanNames(HttpClient client) =>
        (await client.GetFromJsonAsync<SyncPageDto>("/sync/beans?pageSize=100", Ct))!.Records.Select(r => r.Entity["name"]!.GetValue<string>()).ToList();

    [Fact]
    public async Task Joining_another_household_keeps_the_first_and_switching_changes_what_the_token_sees()
    {
        var home = await NewPerson("Home owner");
        var office = await NewPerson("Office owner");
        var me = await NewPerson("Me");
        (await Join(me, await Invite(home))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Join(me, await Invite(office))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await PutBean(await ClientFor(office), "Office beans");

        (await Households(me)).Count.ShouldBe(3); // my own, home and office
        (await Households(me)).Single(h => h.IsActive).Id.ShouldBe(office.User.HouseholdId);
        (await BeanNames(await ClientFor(me))).ShouldBe(["Office beans"]);

        using var switched = await me.Client.PostAsJsonAsync("/households/mine/switch", new SwitchRequest(home.User.HouseholdId), Ct);
        switched.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Body<HouseholdDto>(switched)).Id.ShouldBe(home.User.HouseholdId);
        (await NowSignedIn(me)).HouseholdId.ShouldBe(home.User.HouseholdId);
        (await BeanNames(await ClientFor(me))).ShouldBeEmpty(); // the office's data does not leak into home
    }

    [Fact]
    public async Task Switching_to_a_household_you_are_not_in_is_refused()
    {
        var me = await NewPerson("Me");
        var stranger = await NewPerson("Stranger");

        (await me.Client.PostAsJsonAsync("/households/mine/switch", new SwitchRequest(stranger.User.HouseholdId), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await me.Client.PostAsJsonAsync("/households/mine/switch", new SwitchRequest("nope"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Households(me)).Single().IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task The_list_marks_the_active_personal_and_owned_households()
    {
        var owner = await NewPerson("Owner");
        var me = await NewPerson("Me");
        (await Join(me, await Invite(owner))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var list = await Households(me);

        list.Count.ShouldBe(2);
        var own = list.Single(h => h.Id == me.User.HouseholdId);
        own.IsPersonal.ShouldBeTrue();
        own.IsOwner.ShouldBeTrue();
        own.IsActive.ShouldBeFalse();
        var joined = list.Single(h => h.Id == owner.User.HouseholdId);
        joined.IsActive.ShouldBeTrue();
        joined.IsOwner.ShouldBeFalse();
        joined.IsPersonal.ShouldBeFalse();
        joined.MemberCount.ShouldBe(2);
        (await Households(await NewPerson("Alone"))).Single().MemberCount.ShouldBe(1);
    }

    [Fact]
    public async Task Leaving_one_household_goes_back_to_another_of_mine_and_the_only_one_cannot_be_left()
    {
        var owner = await NewPerson("Owner");
        var me = await NewPerson("Me");
        (await Join(me, await Invite(owner))).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var left = await me.Client.PostAsync("/households/mine/leave", null, Ct);

        left.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Body<HouseholdDto>(left)).Id.ShouldBe(me.User.HouseholdId);
        (await Households(me)).Select(h => h.Id).ShouldBe([me.User.HouseholdId]);
        (await me.Client.PostAsync("/households/mine/leave", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Joining_a_household_you_are_already_in_is_refused()
    {
        var owner = await NewPerson("Owner");
        var me = await NewPerson("Me");
        (await Join(me, await Invite(owner))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await Join(me, await Invite(owner))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_member_can_leave_and_gets_a_household_of_their_own_but_the_owner_cannot_while_others_are_in()
    {
        var owner = await NewPerson("Owner");
        var member = await NewPerson("Member");
        (await Join(member, await Invite(owner))).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var ownerLeaves = await owner.Client.PostAsync("/households/mine/leave", null, Ct);
        ownerLeaves.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var joined = await Refreshed(member);
        using var memberClient = Authorized(factory, joined.AccessToken);
        using var left = await memberClient.PostAsync("/households/mine/leave", null, Ct);
        left.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Body<HouseholdDto>(left)).Id.ShouldBe(member.User.HouseholdId);
        // Refresh tokens rotate: the second refresh must use the token the first one returned.
        (await Refreshed(member with { Tokens = joined })).HouseholdId.ShouldBe(member.User.HouseholdId);
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
