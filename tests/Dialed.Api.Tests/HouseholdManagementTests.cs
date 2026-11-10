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

/// <summary>Creating, renaming and deleting households (#70): only the owner renames or deletes, and the last household stays.</summary>
public sealed class HouseholdManagementTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
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

    private async Task<AppUser> Current(AppUser person) => (await Service<IUserDirectory>().FindByIdAsync(person.Id, Ct))!;

    /// <summary>A client whose token carries the household the person is in right now, as after <c>/auth/refresh</c>.</summary>
    private async Task<HttpClient> ClientFor(AppUser person)
    {
        var tokens = await Service<RefreshTokenService>().SignInAsync(await Current(person), Ct);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private static async Task<T> Body<T>(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<T>(Ct))!;

    private async Task<string> Invite(AppUser owner)
    {
        using var client = await ClientFor(owner);
        using var response = await client.PostAsync("/households/mine/invites", null, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await Body<InviteDto>(response)).Code;
    }

    private async Task<HttpResponseMessage> Join(AppUser person, string code)
    {
        using var client = await ClientFor(person);
        return await client.PostAsJsonAsync("/households/mine/join", new JoinRequest(code), Ct);
    }

    private async Task<HouseholdDto> Create(AppUser person, string name)
    {
        using var client = await ClientFor(person);
        using var response = await client.PostAsJsonAsync("/households", new HouseholdNameRequest(name), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await Body<HouseholdDto>(response);
    }

    private async Task PutBean(AppUser person, string name)
    {
        using var client = await ClientFor(person);
        var id = Guid.NewGuid().ToString();
        using var response = await client.PutAsJsonAsync($"/sync/beans/{id}", new JsonObject { ["id"] = id, ["name"] = name, ["deleted"] = false }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<int> BeanCount(string householdId) =>
        (await Service<ISyncStore>().PullAsync(householdId, "nobody", "beans", null, null, 100, Ct)).Rows.Count;

    [Fact]
    public async Task Creating_a_household_makes_the_person_its_owner_and_moves_them_to_it()
    {
        var thomas = await NewUser("Thomas");

        var office = await Create(thomas, "  Office  ");

        office.Name.ShouldBe("Office");
        office.YouAreOwner.ShouldBeTrue();
        office.Members.Select(m => m.UserId).ShouldBe([thomas.Id]);
        var now = await Current(thomas);
        now.HouseholdId.ShouldBe(office.Id); // the next token carries it
        now.IsMemberOf(thomas.PersonalHouseholdId).ShouldBeTrue(); // the earlier household stays

        using var client = await ClientFor(thomas);
        var list = await Body<List<HouseholdSummaryDto>>(await client.GetAsync("/households", Ct));
        list.Select(h => h.Name).ShouldBe(["Office", "Thomas's household"], ignoreOrder: true);
        list.Single(h => h.IsActive).Id.ShouldBe(office.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A name that is far too long for a household because it goes on and on and on")]
    public async Task A_household_needs_a_sensible_name(string name)
    {
        var thomas = await NewUser("Thomas");
        using var client = await ClientFor(thomas);

        using var created = await client.PostAsJsonAsync("/households", new HouseholdNameRequest(name), Ct);
        using var renamed = await client.PutAsJsonAsync("/households/mine/name", new HouseholdNameRequest(name), Ct);

        created.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        renamed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Current(thomas)).AllHouseholdIds.ShouldBe([thomas.PersonalHouseholdId]);
    }

    [Fact]
    public async Task One_person_can_create_only_so_many_households()
    {
        var thomas = await NewUser("Thomas");
        for (var i = 1; i < HouseholdService.MaxHouseholdsPerPerson; i++)
        {
            await Create(thomas, $"Household {i}");
        }

        var result = await Service<HouseholdService>().CreateAsync(thomas.Id, "One too many", Ct);

        result.Outcome.ShouldBe(HouseholdOutcome.Conflict);
    }

    [Fact]
    public async Task The_owner_renames_the_household_and_everyone_sees_the_new_name()
    {
        var owner = await NewUser("Owner");
        var member = await NewUser("Member");
        (await Join(member, await Invite(owner))).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var ownerClient = await ClientFor(owner);
        using var renamed = await ownerClient.PutAsJsonAsync("/households/mine/name", new HouseholdNameRequest("The coffee corner"), Ct);

        renamed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Body<HouseholdDto>(renamed)).Name.ShouldBe("The coffee corner");
        using var memberClient = await ClientFor(member);
        (await Body<HouseholdDto>(await memberClient.GetAsync("/households/mine", Ct))).Name.ShouldBe("The coffee corner");
    }

    [Fact]
    public async Task A_member_cannot_rename_or_delete_the_household()
    {
        var owner = await NewUser("Owner");
        var member = await NewUser("Member");
        (await Join(member, await Invite(owner))).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var client = await ClientFor(member);

        using var renamed = await client.PutAsJsonAsync("/households/mine/name", new HouseholdNameRequest("Mine now"), Ct);
        using var deleted = await client.DeleteAsync("/households/mine", Ct);

        renamed.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        deleted.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Service<IHouseholdStore>().FindAsync(owner.PersonalHouseholdId, Ct))!.Name.ShouldBe("Owner's household");
    }

    [Fact]
    public async Task The_last_household_cannot_be_deleted()
    {
        var thomas = await NewUser("Thomas");
        await PutBean(thomas, "Kept");
        using var client = await ClientFor(thomas);

        using var deleted = await client.DeleteAsync("/households/mine", Ct);

        deleted.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BeanCount(thomas.PersonalHouseholdId)).ShouldBe(1);
    }

    [Fact]
    public async Task Deleting_a_household_removes_its_data_and_moves_everyone_out()
    {
        var owner = await NewUser("Owner");
        var traveller = await NewUser("Traveller"); // has a household of their own to fall back to
        var guest = await NewUser("Guest");
        var office = await Create(owner, "Office");
        (await Join(traveller, await Invite(owner))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Join(guest, await Invite(owner))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await PutBean(owner, "Office bean");
        await PutBean(traveller, "Another office bean");
        var openCode = await Invite(owner);

        // The guest only has the office: drop their personal household from their memberships.
        await Service<IUserDirectory>().RemoveMembershipAsync(guest.Id, guest.PersonalHouseholdId, Ct);
        (await Current(guest)).AllHouseholdIds.ShouldBe([office.Id]);

        using var ownerClient = await ClientFor(owner);
        using var deleted = await ownerClient.DeleteAsync("/households/mine", Ct);

        deleted.StatusCode.ShouldBe(HttpStatusCode.OK, await deleted.Content.ReadAsStringAsync(Ct));
        (await Body<HouseholdDto>(deleted)).Id.ShouldBe(owner.PersonalHouseholdId);
        (await BeanCount(office.Id)).ShouldBe(0);
        (await Service<IHouseholdStore>().FindAsync(office.Id, Ct)).ShouldBeNull();
        (await Join(await NewUser("Late"), openCode)).StatusCode.ShouldBe(HttpStatusCode.BadRequest); // its invites went with it

        // Nobody belongs to it any more, so the next token carries another household and devices purge the old one's records.
        foreach (var person in new[] { owner, traveller, guest })
        {
            var now = await Current(person);
            now.IsMemberOf(office.Id).ShouldBeFalse();
            now.HouseholdId.ShouldBe(person.PersonalHouseholdId);
        }

        (await Service<IHouseholdStore>().FindAsync(guest.PersonalHouseholdId, Ct))!.OwnerId.ShouldBe(guest.Id);
    }

    [Fact]
    public async Task A_household_that_is_a_members_only_personal_one_cannot_be_deleted_from_under_them()
    {
        var giver = await NewUser("Giver");
        var owner = await NewUser("Owner");
        (await Join(owner, await Invite(giver))).StatusCode.ShouldBe(HttpStatusCode.OK);
        using (var giverClient = await ClientFor(giver))
        {
            (await giverClient.PostAsJsonAsync("/households/mine/transfer", new TransferRequest(owner.Id), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var ownerClient = await ClientFor(owner);
        using var deleted = await ownerClient.DeleteAsync("/households/mine", Ct);

        // The giver would get the same household id back, empty, and their devices would not notice: they have to be removed first.
        deleted.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Current(giver)).HouseholdId.ShouldBe(giver.PersonalHouseholdId);
    }
}
