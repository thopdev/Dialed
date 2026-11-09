using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dialed.Api.Account;
using Dialed.Api.Auth;
using Dialed.Api.Friends;
using Dialed.Api.Households;
using Dialed.Api.Sync;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.Api.Tests;

/// <summary>Things that look across all the households a person belongs to (#73): friends, export and account deletion.</summary>
public sealed class MultiHouseholdTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private T Service<T>() where T : notnull => factory.Services.GetRequiredService<T>();

    private async Task<AppUser> NewUser(string name)
    {
        var id = Guid.NewGuid().ToString("N");
        var user = new AppUser(id, $"{id}@example.com", name, HouseholdService.PersonalHouseholdId(id));
        await Service<IUserDirectory>().AddAsync(user, Ct);
        return user;
    }

    /// <summary>A client whose token carries the household the person is in right now.</summary>
    private async Task<HttpClient> ClientFor(AppUser person)
    {
        var current = (await Service<IUserDirectory>().FindByIdAsync(person.Id, Ct))!;
        var tokens = await Service<RefreshTokenService>().SignInAsync(current, Ct);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private async Task PutBean(AppUser person, string name, int visibility)
    {
        using var client = await ClientFor(person);
        var id = Guid.NewGuid().ToString();
        using var response = await client.PutAsJsonAsync($"/sync/beans/{id}", new JsonObject { ["id"] = id, ["name"] = name, ["deleted"] = false, ["visibility"] = visibility }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task Join(AppUser person, AppUser owner)
    {
        using var ownerClient = await ClientFor(owner);
        using var invite = await ownerClient.PostAsync("/households/mine/invites", null, Ct);
        var code = (await invite.Content.ReadFromJsonAsync<InviteDto>(Ct))!.Code;
        using var personClient = await ClientFor(person);
        using var joined = await personClient.PostAsJsonAsync("/households/mine/join", new JoinRequest(code), Ct);
        joined.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task Follow(AppUser follower, AppUser followee)
    {
        var friends = Service<FriendService>();
        var code = (await friends.GetCodeAsync(followee.Id, Ct)).Code;
        (await friends.FollowAsync(follower.Id, code, Ct)).Succeeded.ShouldBeTrue();
        (await friends.AcceptAsync(followee.Id, follower.Id, Ct)).Succeeded.ShouldBeTrue();
    }

    private async Task<string[]> SharedBeans(AppUser viewer, AppUser owner) =>
        [.. (await Service<FriendService>().SharedAsync(viewer.Id, owner.Id, "beans", 0, 50, Ct))!.Items.Select(i => i["name"]!.GetValue<string>()).Order()];

    [Fact]
    public async Task A_follower_sees_what_a_person_shares_from_every_household_they_belong_to()
    {
        var owner = await NewUser("Owner");
        var office = await NewUser("Office");
        var fan = await NewUser("Fan");
        await PutBean(owner, "Home friends", VisibilityRules.Friends);
        await PutBean(owner, "Home private", VisibilityRules.Private);
        await Join(owner, office); // the owner now works in the office household
        await PutBean(owner, "Office friends", VisibilityRules.Friends);
        await PutBean(owner, "Office household only", VisibilityRules.Household);
        await Follow(fan, owner);

        (await SharedBeans(fan, owner)).ShouldBe(["Home friends", "Office friends"]);
    }

    [Fact]
    public async Task A_member_of_one_of_the_households_also_sees_what_is_shared_with_that_household()
    {
        var owner = await NewUser("Owner");
        var office = await NewUser("Office");
        var colleague = await NewUser("Colleague");
        await Join(owner, office);
        await Join(colleague, office);
        await PutBean(owner, "Office household only", VisibilityRules.Household);
        await PutBean(office, "Not mine", VisibilityRules.Household);

        // Not a follower, but in the same household: sees the household items, not the owner's private or other households' ones.
        (await SharedBeans(colleague, owner)).ShouldBe(["Office household only"]);
    }

    [Fact]
    public async Task A_stranger_sees_nothing()
    {
        var owner = await NewUser("Owner");
        var stranger = await NewUser("Stranger");
        await PutBean(owner, "Hidden", VisibilityRules.Friends);

        (await Service<FriendService>().SharedAsync(stranger.Id, owner.Id, "beans", 0, 50, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Export_holds_what_the_person_made_in_every_household()
    {
        var person = await NewUser("Traveller");
        var office = await NewUser("Office");
        await PutBean(person, "Mine at home", VisibilityRules.Household);
        await Join(person, office);
        await PutBean(person, "Mine at the office", VisibilityRules.Household);
        await PutBean(office, "Someone else's", VisibilityRules.Household);

        var export = (await Service<AccountExporter>().ExportAsync(person.Id, Ct))!;

        export.Items["beans"].Select(b => b["name"]!.GetValue<string>()).Order().ShouldBe(["Mine at home", "Mine at the office"]);
    }

    [Fact]
    public async Task Deleting_an_account_cleans_up_every_household()
    {
        var person = await NewUser("Leaver");
        var office = await NewUser("Office");
        await PutBean(person, "Home bean", VisibilityRules.Household);
        await Join(person, office);
        await PutBean(person, "Office bean", VisibilityRules.Household);
        var sync = Service<ISyncStore>();

        (await Service<AccountDeleter>().DeleteAsync(person.Id, Ct)).ShouldBeTrue();

        // The household they were alone in is gone with its data; the shared one keeps the item, no longer pointing to them.
        (await sync.ListAuthoredAsync(person.HouseholdId, person.Id, "beans", Ct)).ShouldBeEmpty();
        var shared = await sync.PullAsync(office.HouseholdId, office.Id, "beans", null, null, 100, Ct);
        shared.Rows.Single().Entity["createdBy"]!.GetValue<string>().ShouldBe(AccountDeleter.DeletedUser);
        (await Service<IUserDirectory>().FindByIdAsync(person.Id, Ct)).ShouldBeNull();
    }
}
