using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dialed.Api.Auth;
using Dialed.Api.Friends;
using Dialed.Api.Households;
using Dialed.Api.Passkeys;
using Dialed.Api.PublicProfiles;
using Dialed.Api.Sync;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.Api.Tests;

public sealed class ModeratedApp : WebApplicationFactory<Program>
{
    public const string AdminKey = "test-admin-key";

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("Beta:AdminKey", AdminKey);
        builder.UseSetting("Reports:OwnerEmail", "owner@dialed.test");
    }
}

/// <summary>Opt-in public profiles (#106): who sees what, finding people, reports and moderation.</summary>
public sealed class PublicProfileTests(ModeratedApp app) : IClassFixture<ModeratedApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Person(AppUser User, HttpClient Client);

    private async Task<Person> NewPerson(string name)
    {
        var id = Guid.NewGuid().ToString("N");
        var user = new AppUser(id, $"{id}@example.com", name, HouseholdService.PersonalHouseholdId(id));
        await app.Services.GetRequiredService<IUserDirectory>().AddAsync(user, Ct);
        var tokens = await app.Services.GetRequiredService<RefreshTokenService>().SignInAsync(user, Ct);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return new Person(user, client);
    }

    private static string Unique(string name) => $"{name}{Guid.NewGuid().ToString("N")[..6]}";

    private static async Task<string> Put(Person owner, string table, string name, int visibility)
    {
        var id = Guid.CreateVersion7().ToString();
        using var response = await owner.Client.PutAsJsonAsync($"/sync/{table}/{id}", new JsonObject { ["id"] = id, ["name"] = name, ["deleted"] = false, ["visibility"] = visibility }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return id;
    }

    private static Task SetProfile(Person person, bool enabled) => person.Client.PutAsJsonAsync("/profile/public", new PublicProfileRequest(enabled), Ct);

    private static async Task<string[]> SharedNames(Person viewer, Person owner, string table) =>
        [.. (await viewer.Client.GetFromJsonAsync<SharedPageDto>($"/friends/{owner.User.Id}/shared/{table}", Ct))!.Items.Select(i => i["name"]!.GetValue<string>()).Order()];

    private static Task<HttpResponseMessage> Profile(Person viewer, Person owner) => viewer.Client.GetAsync($"/friends/{owner.User.Id}/profile", Ct);

    private static async Task<string[]> Search(Person viewer, string query) =>
        [.. (await viewer.Client.GetFromJsonAsync<PersonDto[]>($"/people/search?q={Uri.EscapeDataString(query)}", Ct))!.Select(p => p.DisplayName)];

    private static async Task<Person> Befriend(Person follower, Person followee)
    {
        var code = (await followee.Client.GetFromJsonAsync<FriendCodeDto>("/friends/code", Ct))!.Code;
        await follower.Client.PostAsJsonAsync("/friends/follow", new FollowRequest(code), Ct);
        await followee.Client.PostAsync($"/friends/requests/{follower.User.Id}/accept", null, Ct);
        return follower;
    }

    private HttpClient Admin(string? key = ModeratedApp.AdminKey)
    {
        var client = app.CreateClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add("X-Admin-Key", key);
        }

        return client;
    }

    // ---- The switch and what strangers see ----------------------------------------------------

    [Fact]
    public async Task The_profile_is_off_by_default_and_the_switch_is_remembered()
    {
        var person = await NewPerson("Pia");
        (await person.Client.GetFromJsonAsync<PublicProfileDto>("/profile/public", Ct))!.Enabled.ShouldBeFalse();

        await SetProfile(person, true);

        (await person.Client.GetFromJsonAsync<PublicProfileDto>("/profile/public", Ct))!.Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task A_stranger_sees_nothing_of_a_profile_that_is_off_even_when_items_are_public()
    {
        var owner = await NewPerson("Olga");
        var stranger = await NewPerson("Sam");
        await Put(owner, "beans", "Quiet bean", VisibilityRules.Public);

        (await Profile(stranger, owner)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await stranger.Client.GetAsync($"/friends/{owner.User.Id}/shared/beans", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_stranger_sees_only_the_public_beans_and_recipes_of_a_public_profile()
    {
        var owner = await NewPerson("Olga");
        var stranger = await NewPerson("Sam");
        await SetProfile(owner, true);
        await Put(owner, "beans", "Public bean", VisibilityRules.Public);
        await Put(owner, "beans", "Friends bean", VisibilityRules.Friends);
        await Put(owner, "beans", "Household bean", VisibilityRules.Household);
        await Put(owner, "beans", "Private bean", VisibilityRules.Private);
        await Put(owner, "recipes", "Public recipe", VisibilityRules.Public);
        await Put(owner, "recipes", "Friends recipe", VisibilityRules.Friends);
        await Put(owner, "brews", "A brew", VisibilityRules.Friends);

        (await SharedNames(stranger, owner, "beans")).ShouldBe(["Public bean"]);
        (await SharedNames(stranger, owner, "recipes")).ShouldBe(["Public recipe"]);
        (await stranger.Client.GetAsync($"/friends/{owner.User.Id}/shared/brews", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var header = (await (await Profile(stranger, owner)).Content.ReadFromJsonAsync<FriendProfileDto>(Ct))!;
        header.DisplayName.ShouldBe("Olga");
        (header.Beans, header.Recipes, header.Brews, header.Followers).ShouldBe((1, 1, 0, 0));
    }

    [Fact]
    public async Task Switching_the_profile_off_hides_it_at_once_without_changing_the_items()
    {
        var owner = await NewPerson("Olga");
        var stranger = await NewPerson("Sam");
        var follower = await Befriend(await NewPerson("Fay"), owner);
        await SetProfile(owner, true);
        var bean = await Put(owner, "beans", "Public bean", VisibilityRules.Public);
        (await SharedNames(stranger, owner, "beans")).ShouldBe(["Public bean"]);

        await SetProfile(owner, false);

        (await Profile(stranger, owner)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SharedNames(follower, owner, "beans")).ShouldBe(["Public bean"]); // a follower still sees it as a friends item
        var row = (await owner.Client.GetFromJsonAsync<SyncPageDto>("/sync/beans", Ct))!.Records.Single(r => r.Entity["id"]!.GetValue<string>() == bean);
        row.Entity["visibility"]!.GetValue<int>().ShouldBe(VisibilityRules.Public);
    }

    [Fact]
    public async Task Un_sharing_an_item_takes_effect_on_the_next_request()
    {
        var owner = await NewPerson("Olga");
        var stranger = await NewPerson("Sam");
        await SetProfile(owner, true);
        var id = await Put(owner, "beans", "Now public", VisibilityRules.Public);
        (await SharedNames(stranger, owner, "beans")).ShouldBe(["Now public"]);
        var current = (await owner.Client.GetFromJsonAsync<SyncPageDto>("/sync/beans", Ct))!.Records.Single(r => r.Entity["id"]!.GetValue<string>() == id);

        var update = (JsonObject)current.Entity.DeepClone();
        update["visibility"] = VisibilityRules.Household;
        using var response = await owner.Client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/sync/beans/{id}") { Content = JsonContent.Create(update), Headers = { { "If-Match", $"\"{current.Version}\"" } } }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await SharedNames(stranger, owner, "beans")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("brews")]
    [InlineData("bags")]
    [InlineData("photos")]
    [InlineData("equipment")]
    public async Task Public_is_refused_for_everything_but_beans_recipes_and_vendors(string table)
    {
        var person = await NewPerson("Pia");
        var id = Guid.CreateVersion7().ToString();

        using var response = await person.Client.PutAsJsonAsync($"/sync/{table}/{id}", new JsonObject { ["id"] = id, ["deleted"] = false, ["visibility"] = VisibilityRules.Public }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Public_vendors_are_allowed()
    {
        var person = await NewPerson("Pia");

        await Put(person, "vendors", "Friedhats", VisibilityRules.Public);
    }

    [Fact]
    public async Task Someone_who_is_blocked_either_way_sees_nothing_and_is_not_found()
    {
        var owner = await NewPerson("Olga");
        var asked = await NewPerson(Unique("Blocky"));
        var blocker = await NewPerson("Bea");
        await SetProfile(owner, true);
        await SetProfile(asked, true);
        await Put(owner, "beans", "Public bean", VisibilityRules.Public);

        await owner.Client.PostAsync($"/friends/blocks/{blocker.User.Id}", null, Ct);
        await blocker.Client.PostAsync($"/friends/blocks/{asked.User.Id}", null, Ct);

        (await Profile(blocker, owner)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Search(blocker, asked.User.DisplayName)).ShouldBeEmpty();
        (await Search(asked, blocker.User.DisplayName)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_stranger_can_copy_a_public_bean_like_a_friends_item()
    {
        var owner = await NewPerson("Olga");
        var stranger = await NewPerson("Sam");
        await SetProfile(owner, true);
        var bean = await Put(owner, "beans", "S1 Kenya", VisibilityRules.Public);
        var hidden = await Put(owner, "beans", "Hidden", VisibilityRules.Friends);

        var item = (await stranger.Client.GetFromJsonAsync<SharedItemDto>($"/friends/{owner.User.Id}/shared/beans/{bean}", Ct))!;

        item.Item["name"]!.GetValue<string>().ShouldBe("S1 Kenya");
        item.OwnerName.ShouldBe("Olga");
        (await stranger.Client.GetAsync($"/friends/{owner.User.Id}/shared/beans/{hidden}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- Finding people -----------------------------------------------------------------------

    [Fact]
    public async Task Search_finds_public_profiles_by_name_with_prefix_and_typo_tolerance()
    {
        var token = Unique("Zorro");
        var found = await NewPerson($"{token} Beans");
        var off = await NewPerson($"{token} Hidden");
        var searcher = await NewPerson("Sam");
        await SetProfile(found, true);

        (await Search(searcher, token)).ShouldBe([$"{token} Beans"]);
        (await Search(searcher, token[..^1] + "x")).ShouldContain($"{token} Beans"); // one typo
        (await Search(searcher, "Hidden")).ShouldBeEmpty();
        off.User.DisplayName.ShouldContain("Hidden");
    }

    [Fact]
    public async Task Search_needs_two_characters_returns_at_most_twenty_and_leaves_out_the_searcher()
    {
        var token = Unique("Crowd");
        for (var i = 0; i < 25; i++)
        {
            var person = await NewPerson($"{token} {i:D2}");
            await SetProfile(person, true);
        }

        var me = await NewPerson($"{token} me");
        await SetProfile(me, true);

        (await Search(me, "a")).ShouldBeEmpty();
        (await Search(me, "")).ShouldBeEmpty();
        var results = await Search(me, token);
        results.Length.ShouldBe(PublicProfileService.MaxResults);
        results.ShouldNotContain($"{token} me");
    }

    [Fact]
    public async Task A_profile_that_is_switched_off_again_is_no_longer_found()
    {
        var token = Unique("Vanish");
        var person = await NewPerson(token);
        var searcher = await NewPerson("Sam");
        await SetProfile(person, true);
        (await Search(searcher, token)).ShouldBe([token]);

        await SetProfile(person, false);

        (await Search(searcher, token)).ShouldBeEmpty();
    }

    // ---- Reports ------------------------------------------------------------------------------

    private static Task<HttpResponseMessage> Report(Person reporter, Person owner, string target = "profile", string? itemId = null, string reason = "spam", string? note = null) =>
        reporter.Client.PostAsJsonAsync("/reports", new ReportRequest(owner.User.Id, target, itemId, reason, note), Ct);

    [Fact]
    public async Task A_report_is_stored_mailed_to_the_owner_and_confirmed_with_no_content()
    {
        var owner = await NewPerson(Unique("Spammy"));
        var reporter = await NewPerson("Rae");
        await SetProfile(owner, true);
        var bean = await Put(owner, "beans", "Buy now", VisibilityRules.Public);

        (await Report(reporter, owner, "bean", bean, "offensive", "rude text")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var mail = app.Services.GetRequiredService<OutboxEmailSender>().Sent.Single(m => m.Body.Contains(bean));
        mail.To.ShouldBe("owner@dialed.test");
        mail.Body.ShouldContain("offensive");
        mail.Body.ShouldContain("rude text");
        var listed = (await Admin().GetFromJsonAsync<ReportDto[]>("/admin/reports", Ct))!.Single(r => r.ItemId == bean);
        (listed.ReporterId, listed.OwnerId, listed.Target, listed.Reason).ShouldBe((reporter.User.Id, owner.User.Id, "bean", "offensive"));
    }

    [Fact]
    public async Task The_same_report_twice_is_stored_and_mailed_once_and_looks_the_same_to_the_reporter()
    {
        var owner = await NewPerson(Unique("Spammy"));
        var reporter = await NewPerson("Rae");
        await SetProfile(owner, true);

        (await Report(reporter, owner)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Report(reporter, owner, reason: "other")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await Admin().GetFromJsonAsync<ReportDto[]>("/admin/reports", Ct))!.Count(r => r.OwnerId == owner.User.Id).ShouldBe(1);
        app.Services.GetRequiredService<OutboxEmailSender>().Sent.Count(m => m.Body.Contains(owner.User.Id)).ShouldBe(1);
    }

    [Theory]
    [InlineData("nonsense", "spam", null)]
    [InlineData("profile", "nonsense", null)]
    [InlineData("bean", "spam", null)] // an item report names the item
    public async Task An_incomplete_report_is_refused(string target, string reason, string? itemId)
    {
        var owner = await NewPerson("Olga");
        var reporter = await NewPerson("Rae");

        (await Report(reporter, owner, target, itemId, reason)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_report_about_nobody_yourself_or_a_blocker_is_not_found()
    {
        var owner = await NewPerson("Olga");
        var reporter = await NewPerson("Rae");
        await owner.Client.PostAsync($"/friends/blocks/{reporter.User.Id}", null, Ct);

        (await Report(reporter, owner)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Report(reporter, reporter)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await reporter.Client.PostAsJsonAsync("/reports", new ReportRequest("nobody", "profile", null, "spam", null), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reports_are_rate_limited()
    {
        await using var limited = app.WithWebHostBuilder(b => b.UseSetting("Protection:ReportsPerMinute", "2"));
        var id = Guid.NewGuid().ToString("N");
        var user = new AppUser(id, $"{id}@example.com", "Rae", HouseholdService.PersonalHouseholdId(id));
        await limited.Services.GetRequiredService<IUserDirectory>().AddAsync(user, Ct);
        var tokens = await limited.Services.GetRequiredService<RefreshTokenService>().SignInAsync(user, Ct);
        using var client = limited.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 4; i++)
        {
            using var response = await client.PostAsJsonAsync("/reports", new ReportRequest("nobody", "profile", null, "spam", null), Ct);
            statuses.Add(response.StatusCode);
        }

        statuses.ShouldContain(HttpStatusCode.TooManyRequests);
    }

    // ---- Moderation ---------------------------------------------------------------------------

    [Fact]
    public async Task A_suspended_person_is_hidden_from_everyone_else_without_deleting_anything_and_can_be_restored()
    {
        var token = Unique("Banned");
        var owner = await NewPerson(token);
        var stranger = await NewPerson("Sam");
        var follower = await Befriend(await NewPerson("Fay"), owner);
        var housemate = await NewPerson("Hal");
        await SetProfile(owner, true);
        await Put(owner, "beans", "Public bean", VisibilityRules.Public);
        (await SharedNames(stranger, owner, "beans")).ShouldBe(["Public bean"]);

        (await Admin().PostAsync($"/admin/users/{owner.User.Id}/suspend", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await Profile(stranger, owner)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Profile(follower, owner)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Search(stranger, token)).ShouldBeEmpty();
        (await Profile(owner, owner)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.Client.GetFromJsonAsync<SyncPageDto>("/sync/beans", Ct))!.Records.Count.ShouldBe(1); // nothing was deleted
        housemate.User.DisplayName.ShouldBe("Hal");

        (await Admin().PostAsync($"/admin/users/{owner.User.Id}/restore", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await SharedNames(stranger, owner, "beans")).ShouldBe(["Public bean"]);
        (await Search(stranger, token)).ShouldBe([token]);
    }

    [Fact]
    public async Task The_admin_endpoints_need_the_admin_key()
    {
        var person = await NewPerson("Pia");

        (await Admin(key: null).PostAsync($"/admin/users/{person.User.Id}/suspend", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Admin(key: "wrong").GetAsync("/admin/reports", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Admin().PostAsync("/admin/users/nobody/suspend", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await person.Client.GetAsync("/admin/reports", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Without_an_admin_key_configured_the_admin_endpoints_do_not_exist()
    {
        using var open = app.WithWebHostBuilder(b => b.UseSetting("Beta:AdminKey", "")).CreateClient();

        (await open.GetAsync("/admin/reports", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- Following from a public profile ------------------------------------------------------

    [Fact]
    public async Task A_stranger_can_ask_to_follow_a_public_profile_without_a_code_and_the_profile_says_where_they_stand()
    {
        var owner = await NewPerson("Olga");
        var stranger = await NewPerson("Sam");
        await SetProfile(owner, true);
        (await (await Profile(stranger, owner)).Content.ReadFromJsonAsync<FriendProfileDto>(Ct))!.Relation.ShouldBe("none");

        (await stranger.Client.PostAsync($"/friends/{owner.User.Id}/follow", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await (await Profile(stranger, owner)).Content.ReadFromJsonAsync<FriendProfileDto>(Ct))!.Relation.ShouldBe("requested");
        (await owner.Client.GetFromJsonAsync<PersonDto[]>("/friends/requests", Ct))!.Select(p => p.DisplayName).ShouldBe(["Sam"]);
        await owner.Client.PostAsync($"/friends/requests/{stranger.User.Id}/accept", null, Ct);
        (await (await Profile(stranger, owner)).Content.ReadFromJsonAsync<FriendProfileDto>(Ct))!.Relation.ShouldBe("following");
    }

    [Fact]
    public async Task Following_without_a_code_does_not_work_for_a_profile_that_is_off_or_a_person_who_blocked_you()
    {
        var off = await NewPerson("Off");
        var blocker = await NewPerson("Blocker");
        var stranger = await NewPerson("Sam");
        await SetProfile(blocker, true);
        await blocker.Client.PostAsync($"/friends/blocks/{stranger.User.Id}", null, Ct);

        (await stranger.Client.PostAsync($"/friends/{off.User.Id}/follow", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await stranger.Client.PostAsync($"/friends/{blocker.User.Id}/follow", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await stranger.Client.PostAsync("/friends/nobody/follow", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- Deletion and export ------------------------------------------------------------------

    [Fact]
    public async Task Deleting_the_account_removes_the_profile_from_search_and_pages_at_once()
    {
        var token = Unique("Leaver");
        var owner = await NewPerson(token);
        var stranger = await NewPerson("Sam");
        await SetProfile(owner, true);
        await Put(owner, "beans", "Gone soon", VisibilityRules.Public);
        (await Search(stranger, token)).ShouldBe([token]);

        // The endpoint asks for a passkey ceremony (DeleteAccountTests); here the deletion itself is what counts.
        using (var scope = app.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<Dialed.Api.Account.AccountDeleter>().DeleteAsync(owner.User.Id, Ct)).ShouldBeTrue();
        }

        (await Search(stranger, token)).ShouldBeEmpty();
        (await Profile(stranger, owner)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await app.Services.GetRequiredService<IPublicProfileStore>().GetAsync(owner.User.Id, Ct)).Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task The_export_says_whether_the_profile_is_public_and_keeps_the_public_setting_of_items()
    {
        var person = await NewPerson("Pia");
        await SetProfile(person, true);
        await Put(person, "beans", "Shared widely", VisibilityRules.Public);

        var export = (await person.Client.GetFromJsonAsync<JsonObject>("/account/export", Ct))!;

        export["profile"]!["publicProfile"]!.GetValue<bool>().ShouldBeTrue();
        export["items"]!["beans"]!.AsArray().Single()!["visibility"]!.GetValue<int>().ShouldBe(VisibilityRules.Public);
    }

    [Fact]
    public async Task The_owner_can_list_who_is_suspended()
    {
        var one = await NewPerson(Unique("Susp"));
        var two = await NewPerson(Unique("Fine"));
        await Admin().PostAsync($"/admin/users/{one.User.Id}/suspend", null, Ct);

        var list = (await Admin().GetFromJsonAsync<SuspendedDto[]>("/admin/users/suspended", Ct))!;
        list.ShouldContain(s => s.UserId == one.User.Id && s.DisplayName == one.User.DisplayName);
        list.ShouldNotContain(s => s.UserId == two.User.Id);

        await Admin().PostAsync($"/admin/users/{one.User.Id}/restore", null, Ct);
        (await Admin().GetFromJsonAsync<SuspendedDto[]>("/admin/users/suspended", Ct))!.ShouldNotContain(s => s.UserId == one.User.Id);
        (await Admin(key: null).GetAsync("/admin/users/suspended", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_admin_endpoints_are_rate_limited_so_the_key_cannot_be_guessed()
    {
        await using var limited = app.WithWebHostBuilder(b => b.UseSetting("Protection:AdminRequestsPerMinute", "3"));
        using var client = limited.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", "wrong");
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 8; i++)
        {
            using var response = await client.GetAsync("/admin/reports", Ct);
            statuses.Add(response.StatusCode);
        }

        statuses.Take(3).ShouldAllBe(s => s == HttpStatusCode.Unauthorized);
        statuses.ShouldContain(HttpStatusCode.TooManyRequests);
    }
}
