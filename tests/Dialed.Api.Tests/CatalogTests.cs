using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dialed.Api.Auth;
using Dialed.Api.Catalog;
using Dialed.Api.Households;
using Dialed.Api.PublicProfiles;
using Dialed.Api.Sync;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.Api.Tests;

/// <summary>The bean and roaster catalog built from what people share publicly (#105).</summary>
public sealed class CatalogTests(ModeratedApp app) : IClassFixture<ModeratedApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Person(AppUser User, HttpClient Client);

    private async Task<Person> NewPerson(string name = "Pia", bool publicProfile = true, WebApplicationFactory<Program>? host = null)
    {
        host ??= app;
        var id = Guid.NewGuid().ToString("N");
        var user = new AppUser(id, $"{id}@example.com", name, HouseholdService.PersonalHouseholdId(id));
        await host.Services.GetRequiredService<IUserDirectory>().AddAsync(user, Ct);
        var tokens = await host.Services.GetRequiredService<RefreshTokenService>().SignInAsync(user, Ct);
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var person = new Person(user, client);
        if (publicProfile)
        {
            await client.PutAsJsonAsync("/profile/public", new PublicProfileRequest(true), Ct);
        }

        return person;
    }

    private static string Unique(string name) => $"{name}{Guid.NewGuid().ToString("N")[..6]}";

    private static async Task<string> Put(Person person, string table, JsonObject entity)
    {
        var id = entity["id"]?.GetValue<string>() ?? Guid.CreateVersion7().ToString();
        entity["id"] = id;
        entity["deleted"] = false;
        using var response = await person.Client.PutAsJsonAsync($"/sync/{table}/{id}", entity, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return id;
    }

    /// <summary>A bean the person shares. <paramref name="visibility"/>: 0 private, 1 household, 2 friends, 3 public.</summary>
    private static Task<string> Bean(Person person, string name, string? roasterId = null, int visibility = VisibilityRules.Public, string country = "Ethiopia", string[]? notes = null)
    {
        var bean = new JsonObject
        {
            ["name"] = name,
            ["visibility"] = visibility,
            ["origin"] = new JsonObject { ["country"] = country },
            ["tastingNotes"] = new JsonArray([.. (notes ?? []).Select(n => (JsonNode)n)]),
            ["varieties"] = new JsonArray(),
            ["photoIds"] = new JsonArray(),
        };
        if (roasterId is not null)
        {
            bean["roasterId"] = roasterId;
        }

        return Put(person, "beans", bean);
    }

    private static Task<string> Vendor(Person person, string name, int visibility = VisibilityRules.Public, string? city = null) =>
        Put(person, "vendors", new JsonObject { ["name"] = name, ["kinds"] = 1, ["visibility"] = visibility, ["city"] = city });

    private async Task Rebuild() => await app.Services.GetRequiredService<CatalogService>().RebuildAsync(Ct);

    private async Task<CatalogBeanDto[]> Beans(Person asker, string q, string? roaster = null) =>
        (await asker.Client.GetFromJsonAsync<CatalogBeanDto[]>($"/catalog/beans?q={Uri.EscapeDataString(q)}&roaster={Uri.EscapeDataString(roaster ?? "")}", Ct))!;

    private async Task<CatalogRoasterDto[]> Roasters(Person asker, string q) =>
        (await asker.Client.GetFromJsonAsync<CatalogRoasterDto[]>($"/catalog/roasters?q={Uri.EscapeDataString(q)}", Ct))!;

    private HttpClient Admin(string? key = ModeratedApp.AdminKey)
    {
        var client = app.CreateClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add("X-Admin-Key", key);
        }

        return client;
    }

    // ---- What goes in -------------------------------------------------------------------------

    [Fact]
    public async Task A_bean_two_different_people_made_public_is_in_the_catalog_and_one_person_alone_is_not()
    {
        var name = Unique("Guji");
        var a = await NewPerson();
        var b = await NewPerson();
        await Bean(a, name);
        await Rebuild();
        (await Beans(a, name)).ShouldBeEmpty();

        await Bean(b, name);
        await Rebuild();

        var found = (await Beans(a, name)).ShouldHaveSingleItem();
        found.Name.ShouldBe(name);
        found.People.ShouldBe(2);
        found.Country.ShouldBe("Ethiopia");
    }

    [Fact]
    public async Task Only_public_items_of_public_profiles_count()
    {
        var name = Unique("Mix");
        var a = await NewPerson();
        var privateSharer = await NewPerson();
        var profileOff = await NewPerson(publicProfile: false);
        await Bean(a, name);
        foreach (var visibility in new[] { VisibilityRules.Private, VisibilityRules.Household, VisibilityRules.Friends })
        {
            await Bean(privateSharer, name, visibility: visibility);
        }

        await Bean(profileOff, name);
        await Rebuild();

        (await Beans(a, name)).ShouldBeEmpty(); // one person only: the others never counted
    }

    [Fact]
    public async Task Un_sharing_switching_the_profile_off_suspension_and_deletion_all_drop_a_contribution_on_the_next_build()
    {
        var name = Unique("Leaving");
        var stays = await NewPerson();
        var unshares = await NewPerson();
        var switchesOff = await NewPerson();
        var suspended = await NewPerson();
        var deleted = await NewPerson();
        await Bean(stays, name);
        var unsharedId = await Bean(unshares, name);
        foreach (var person in new[] { switchesOff, suspended, deleted })
        {
            await Bean(person, name);
        }

        await Rebuild();
        (await Beans(stays, name)).ShouldHaveSingleItem().People.ShouldBe(5);

        var current = (await unshares.Client.GetFromJsonAsync<SyncPageDto>("/sync/beans", Ct))!.Records.Single(r => r.Entity["id"]!.GetValue<string>() == unsharedId);
        var update = (JsonObject)current.Entity.DeepClone();
        update["visibility"] = VisibilityRules.Household;
        using var put = await unshares.Client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/sync/beans/{unsharedId}") { Content = JsonContent.Create(update), Headers = { { "If-Match", $"\"{current.Version}\"" } } }, Ct);
        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        await switchesOff.Client.PutAsJsonAsync("/profile/public", new PublicProfileRequest(false), Ct);
        (await Admin().PostAsync($"/admin/users/{suspended.User.Id}/suspend", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using (var scope = app.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<Dialed.Api.Account.AccountDeleter>().DeleteAsync(deleted.User.Id, Ct)).ShouldBeTrue();
        }

        await Rebuild();

        (await Beans(stays, name)).ShouldBeEmpty(); // only one person is left
    }

    [Fact]
    public async Task The_roaster_a_public_bean_names_counts_even_when_the_vendor_itself_is_not_public()
    {
        var roaster = Unique("Roastery");
        var a = await NewPerson();
        var b = await NewPerson();
        await Bean(a, Unique("One"), roasterId: await Vendor(a, roaster, VisibilityRules.Household, city: "Utrecht"));
        await Bean(b, Unique("Two"), roasterId: await Vendor(b, roaster, VisibilityRules.Household, city: "Utrecht"));
        await Rebuild();

        var found = (await Roasters(a, roaster)).ShouldHaveSingleItem();

        found.People.ShouldBe(2);
        found.City.ShouldBe("Utrecht");
    }

    [Fact]
    public async Task Public_vendors_that_are_roasters_count_too_but_shops_do_not()
    {
        var roaster = Unique("Public Roastery");
        var shop = Unique("Only A Shop");
        var a = await NewPerson();
        var b = await NewPerson();
        foreach (var person in new[] { a, b })
        {
            await Vendor(person, roaster);
            await Put(person, "vendors", new JsonObject { ["name"] = shop, ["kinds"] = 2, ["visibility"] = VisibilityRules.Public });
        }

        await Rebuild();

        (await Roasters(a, roaster)).ShouldHaveSingleItem().People.ShouldBe(2);
        (await Roasters(a, shop)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Free_text_keeps_only_terms_two_people_used()
    {
        var name = Unique("Notes");
        var a = await NewPerson();
        var b = await NewPerson();
        await Bean(a, name, notes: ["jasmine", "my secret note"]);
        await Bean(b, name, notes: ["Jasmine"]);
        await Rebuild();

        (await Beans(a, name)).Single().TastingNotes.Select(n => n.ToLowerInvariant()).ShouldBe(["jasmine"]);
    }

    // ---- What comes out -----------------------------------------------------------------------

    [Fact]
    public async Task Answers_hold_no_user_or_household_ids_and_nothing_points_back_to_a_contributor()
    {
        var name = Unique("Private ids");
        var a = await NewPerson();
        var b = await NewPerson();
        var roaster = Unique("R");
        await Bean(a, name, roasterId: await Vendor(a, roaster));
        await Bean(b, name, roasterId: await Vendor(b, roaster));
        await Rebuild();

        using var response = await a.Client.GetAsync($"/catalog/beans?q={Uri.EscapeDataString(name)}", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        text.ShouldContain(name);
        text.ShouldNotContain(a.User.Id);
        text.ShouldNotContain(b.User.Id);
        text.ShouldNotContain(a.User.HouseholdId);
        text.ShouldNotContain("createdBy");
        text.ShouldNotContain("householdId");
    }

    [Fact]
    public async Task Search_needs_two_characters_a_roaster_alone_lists_its_beans_and_results_stop_at_twenty()
    {
        var roaster = Unique("Many");
        var a = await NewPerson();
        var b = await NewPerson();
        var va = await Vendor(a, roaster);
        var vb = await Vendor(b, roaster);
        for (var i = 0; i < 25; i++)
        {
            var name = $"Coffee {roaster} {i:D2}";
            await Bean(a, name, va);
            await Bean(b, name, vb);
        }

        await Rebuild();

        (await Beans(a, "x")).ShouldBeEmpty();
        (await Roasters(a, "")).ShouldBeEmpty();
        (await Beans(a, "", roaster)).Length.ShouldBe(CatalogService.MaxResults);
        (await Beans(a, "Coffee", roaster)).Length.ShouldBe(CatalogService.MaxResults);
    }

    [Fact]
    public async Task Results_are_ranked_by_how_well_they_match_then_by_popularity()
    {
        var token = Unique("Rank");
        var a = await NewPerson();
        var b = await NewPerson();
        var c = await NewPerson();
        var popular = $"{token} Natural";
        var exact = token;
        await Bean(a, popular);
        await Bean(b, popular);
        await Bean(c, popular);
        await Bean(a, exact);
        await Bean(b, exact);
        await Rebuild();

        (await Beans(a, token)).Select(x => x.Name).ShouldBe([exact, popular]);
    }

    [Fact]
    public async Task The_catalog_is_for_signed_in_people_only()
    {
        using var anonymous = app.CreateClient();

        (await anonymous.GetAsync("/catalog/beans?q=guji", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/catalog/roasters?q=guji", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Searches_are_rate_limited()
    {
        await using var limited = app.WithWebHostBuilder(b => b.UseSetting("Protection:FriendsRequestsPerMinute", "3"));
        var person = await NewPerson(host: limited);
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 6; i++)
        {
            using var response = await person.Client.GetAsync("/catalog/beans?q=guji", Ct);
            statuses.Add(response.StatusCode);
        }

        statuses.ShouldContain(HttpStatusCode.TooManyRequests);
    }

    // ---- The threshold ------------------------------------------------------------------------

    [Theory]
    [InlineData("3", 2, false)]
    [InlineData("3", 3, true)]
    [InlineData("1", 2, true)] // 1 is raised to the lowest safe value
    public async Task The_threshold_is_a_setting_and_never_below_two(string setting, int people, bool expected)
    {
        await using var host = app.WithWebHostBuilder(b => b.UseSetting("Catalog:MinContributors", setting));
        var name = Unique("Threshold");
        Person? first = null;
        for (var i = 0; i < people; i++)
        {
            var person = await NewPerson(host: host);
            first ??= person;
            await Bean(person, name);
        }

        await host.Services.GetRequiredService<CatalogService>().RebuildAsync(Ct);

        (await first!.Client.GetFromJsonAsync<CatalogBeanDto[]>($"/catalog/beans?q={Uri.EscapeDataString(name)}", Ct))!.Any().ShouldBe(expected);
    }

    // ---- Moderation ---------------------------------------------------------------------------

    [Fact]
    public async Task The_admin_endpoints_need_the_admin_key()
    {
        var person = await NewPerson();

        (await Admin(key: null).PostAsync("/admin/catalog/rebuild", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Admin(key: "wrong").GetAsync("/admin/catalog/entries", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await person.Client.GetAsync("/admin/catalog/entries", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Admin().PostAsync("/admin/catalog/entries/nothing/hide", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_owner_can_rebuild_on_demand_and_sees_the_entries_with_the_fewest_people_first()
    {
        var thin = Unique("Thin");
        var thick = Unique("Thick");
        var people = new[] { await NewPerson(), await NewPerson(), await NewPerson() };
        foreach (var person in people.Take(2))
        {
            await Bean(person, thin);
        }

        foreach (var person in people)
        {
            await Bean(person, thick);
        }

        using var rebuilt = await Admin().PostAsync("/admin/catalog/rebuild", null, Ct);
        rebuilt.StatusCode.ShouldBe(HttpStatusCode.OK);

        var entries = (await Admin().GetFromJsonAsync<CatalogEntryAdminDto[]>("/admin/catalog/entries?take=1000", Ct))!;
        var indexThin = Array.FindIndex(entries, e => e.Name == thin);
        var indexThick = Array.FindIndex(entries, e => e.Name == thick);
        indexThin.ShouldBeGreaterThanOrEqualTo(0);
        indexThin.ShouldBeLessThan(indexThick);
        entries.Select(e => e.People).ShouldBe([.. entries.Select(e => e.People).Order()]);
    }

    [Fact]
    public async Task A_hidden_entry_stays_hidden_after_rebuilds_even_when_more_people_share_it()
    {
        var name = Unique("Junk");
        var a = await NewPerson();
        var b = await NewPerson();
        await Bean(a, name);
        await Bean(b, name);
        await Rebuild();
        var id = (await Beans(a, name)).Single().Id;

        (await Admin().PostAsync($"/admin/catalog/entries/{id}/hide", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Beans(a, name)).ShouldBeEmpty();

        await Bean(await NewPerson(), name);
        await Rebuild();
        (await Beans(a, name)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Two_entries_can_be_merged_and_the_merge_survives_a_rebuild()
    {
        var token = Unique("Merge");
        var one = $"{token} Alpha Lot";
        var two = $"{token} Totally Different Name";
        var people = new[] { await NewPerson(), await NewPerson() };
        foreach (var person in people)
        {
            await Bean(person, one);
            await Bean(person, two);
        }

        await Rebuild();
        var entries = (await Admin().GetFromJsonAsync<CatalogEntryAdminDto[]>("/admin/catalog/entries?take=1000", Ct))!.Where(e => e.Name.StartsWith(token)).ToList();
        entries.Count.ShouldBe(2);

        using var merge = await Admin().PostAsJsonAsync("/admin/catalog/merge", new CatalogMergeRequest(entries[0].Id, entries[1].Id), Ct);
        merge.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await Rebuild();

        (await Beans(people[0], token)).ShouldHaveSingleItem().People.ShouldBe(2);
    }

    [Fact]
    public async Task Merging_an_entry_into_itself_or_a_roaster_into_a_bean_is_refused()
    {
        var a = await NewPerson();
        var b = await NewPerson();
        var name = Unique("Self");
        foreach (var person in new[] { a, b })
        {
            await Bean(person, name, await Vendor(person, name));
        }

        await Rebuild();
        var bean = (await Admin().GetFromJsonAsync<CatalogEntryAdminDto[]>("/admin/catalog/entries?take=1000", Ct))!.First(e => e.Kind == "bean" && e.Name == name);
        var roaster = (await Admin().GetFromJsonAsync<CatalogEntryAdminDto[]>("/admin/catalog/entries?take=1000", Ct))!.First(e => e.Kind == "roaster" && e.Name == name);

        (await Admin().PostAsJsonAsync("/admin/catalog/merge", new CatalogMergeRequest(bean.Id, bean.Id), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Admin().PostAsJsonAsync("/admin/catalog/merge", new CatalogMergeRequest(roaster.Id, bean.Id), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- Barcodes -----------------------------------------------------------------------------

    private static async Task<string> BeanWithCodes(Person person, string name, params string[] codes)
    {
        var id = Guid.CreateVersion7().ToString();
        await Put(person, "beans", new JsonObject
        {
            ["id"] = id, ["name"] = name, ["visibility"] = VisibilityRules.Public, ["origin"] = new JsonObject { ["country"] = "Ethiopia" },
            ["tastingNotes"] = new JsonArray(), ["varieties"] = new JsonArray(), ["photoIds"] = new JsonArray(),
            ["barcodes"] = new JsonArray([.. codes.Select(c => (JsonNode)c)]),
        });
        return id;
    }

    private static async Task<CatalogBeanDto[]> ByBarcode(Person asker, string code) =>
        (await asker.Client.GetFromJsonAsync<CatalogBeanDto[]>($"/catalog/barcode?code={Uri.EscapeDataString(code)}", Ct))!;

    [Fact]
    public async Task A_barcode_two_people_entered_finds_the_bean_in_any_spelling()
    {
        var name = Unique("Coded");
        var a = await NewPerson();
        var b = await NewPerson();
        await BeanWithCodes(a, name, "4006381333931", "96385074");
        await BeanWithCodes(b, name, "4006381333931");
        await Rebuild();

        (await ByBarcode(a, "4006381333931")).ShouldHaveSingleItem().Name.ShouldBe(name);
        (await ByBarcode(a, "4 006381 333931")).ShouldHaveSingleItem().Name.ShouldBe(name);
        var entry = (await Beans(a, name)).Single();
        entry.Barcodes.ShouldBe(["4006381333931"]); // the second code was only on one person's bean
        (await ByBarcode(a, "96385074")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_code_with_a_wrong_check_digit_or_nothing_finds_nothing()
    {
        var a = await NewPerson();

        (await ByBarcode(a, "4006381333932")).ShouldBeEmpty();
        (await ByBarcode(a, "")).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_barcode_lookup_is_for_signed_in_people_only()
    {
        using var anonymous = app.CreateClient();

        (await anonymous.GetAsync("/catalog/barcode?code=4006381333931", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
