using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dialed.Api.Auth;
using Dialed.Api.Households;
using Dialed.Api.Push;
using Dialed.Domain.Inventory;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.Api.Tests;

/// <summary>Low stock and bag-at-peak notifications (#259): who is told, when, and how often.</summary>
public sealed class StockAlertTests(PushApp app) : IClassFixture<PushApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 08:30 in Amsterdam on 2026-11-11.
    private static readonly DateTimeOffset Morning = DateTimeOffset.Parse("2026-11-11T07:30:00Z");

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

    private static async Task Put(Person person, string table, JsonObject entity)
    {
        var id = entity["id"]!.GetValue<string>();
        entity["deleted"] = false;
        using var response = await person.Client.PutAsJsonAsync($"/sync/{table}/{id}", entity, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<string> Bean(Person person, string name)
    {
        var id = Guid.CreateVersion7().ToString();
        await Put(person, "beans", new JsonObject { ["id"] = id, ["name"] = name, ["tastingNotes"] = new JsonArray(), ["varieties"] = new JsonArray(), ["photoIds"] = new JsonArray(), ["origin"] = new JsonObject() });
        return id;
    }

    /// <summary>A bag. Status: 0 sealed, 1 open, 2 frozen, 3 finished. Roasted <paramref name="ageDays"/> before 2026-11-11 (medium roast: ready after 7 days, at its best until 21).</summary>
    private static async Task Bag(Person person, string beanId, decimal weightG, int status = 1, int? ageDays = 2)
    {
        var bag = new JsonObject { ["id"] = Guid.CreateVersion7().ToString(), ["beanId"] = beanId, ["weightG"] = weightG, ["status"] = status };
        if (ageDays is { } age)
        {
            bag["roastDate"] = new DateOnly(2026, 11, 11).AddDays(-age).ToString("yyyy-MM-dd");
        }

        await Put(person, "bags", bag);
    }

    private static async Task Subscribe(Person person, string language = "en")
    {
        using var response = await person.Client.PutAsJsonAsync("/push/subscription", new PushSubscribeRequest($"https://push.example.com/send/{Guid.NewGuid():N}", "BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA_0QTpQtUbVlUls0VJXg7A8u-Ts1XbjhazAkj7I99e8QcYP7DkM", "tBHItJI5svbpez7KI4CCXg", language), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private static async Task Switches(Person person, bool low, bool peak, InventorySettings? inventory = null, string? zone = "Europe/Amsterdam")
    {
        using var response = await person.Client.PutAsJsonAsync("/push/settings", new PushSettingsRequest(true, true, low, peak, inventory, zone), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    private Task Run(DateTimeOffset at) => app.Services.GetRequiredService<StockAlertService>().RunAsync(at, Ct);

    private List<PushPayload> Received(Person person) => [.. app.Sender.Sent.Where(s => s.To.UserId == person.User.Id).Select(s => s.Payload)];

    // ---- Low stock ----------------------------------------------------------------------------

    [Fact]
    public async Task Bags_running_low_come_in_one_combined_notification_in_the_language_of_the_device()
    {
        var person = await NewPerson("Pia");
        await Subscribe(person, "nl");
        await Switches(person, low: true, peak: false);
        await Bag(person, await Bean(person, "Guji"), 60m);
        await Bag(person, await Bean(person, "Kiambu"), 40m);
        await Bag(person, await Bean(person, "Plenty"), 250m);

        await Run(Morning);

        var received = Received(person).ShouldHaveSingleItem();
        received.Body.ShouldBe("2 zakken raken op.");
        received.Url.ShouldBe("inventory?show=low");
    }

    [Fact]
    public async Task One_bag_is_named()
    {
        var person = await NewPerson("Pia");
        await Subscribe(person);
        await Switches(person, low: true, peak: false);
        await Bag(person, await Bean(person, "Guji Hambela"), 60m);

        await Run(Morning);

        Received(person).ShouldHaveSingleItem().Body.ShouldBe("Guji Hambela is running low.");
    }

    [Fact]
    public async Task The_threshold_is_the_one_the_app_sent()
    {
        var person = await NewPerson("Pia");
        await Subscribe(person);
        await Switches(person, low: true, peak: false, new InventorySettings(300m, RestWindows.Default));
        await Bag(person, await Bean(person, "Guji"), 250m);

        await Run(Morning);

        Received(person).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Finished_frozen_and_sealed_bags_and_bags_above_the_threshold_are_skipped()
    {
        var person = await NewPerson("Pia");
        await Subscribe(person);
        await Switches(person, low: true, peak: false);
        var bean = await Bean(person, "Guji");
        await Bag(person, bean, 10m, status: 3);
        await Bag(person, bean, 10m, status: 2);
        await Bag(person, bean, 10m, status: 0);
        await Bag(person, bean, 500m);

        await Run(Morning);

        Received(person).ShouldBeEmpty();
    }

    // ---- Peak ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_bag_inside_its_best_window_is_announced_and_one_still_resting_is_not()
    {
        var person = await NewPerson("Pia");
        await Subscribe(person);
        await Switches(person, low: false, peak: true);
        await Bag(person, await Bean(person, "Kiambu"), 250m, ageDays: 14);
        await Bag(person, await Bean(person, "Fresh"), 250m, ageDays: 2);
        await Bag(person, await Bean(person, "Old"), 250m, ageDays: 40);
        await Bag(person, await Bean(person, "NoDate"), 250m, ageDays: null);

        await Run(Morning);

        var received = Received(person).ShouldHaveSingleItem();
        received.Body.ShouldBe("Kiambu is at its peak.");
        received.Url.ShouldBe("inventory?show=peak");
    }

    [Fact]
    public async Task Both_kinds_are_sent_as_two_notifications_and_only_the_ones_switched_on()
    {
        var both = await NewPerson("Both");
        var lowOnly = await NewPerson("Low");
        foreach (var person in new[] { both, lowOnly })
        {
            await Subscribe(person);
            await Bag(person, await Bean(person, "Guji"), 60m, ageDays: 14);
        }

        await Switches(both, low: true, peak: true);
        await Switches(lowOnly, low: true, peak: false);

        await Run(Morning);

        Received(both).Select(p => p.Tag).Order().ShouldBe(["low-stock", "peak"]);
        Received(lowOnly).Select(p => p.Tag).ShouldBe(["low-stock"]);
    }

    // ---- When and how often -------------------------------------------------------------------

    [Fact]
    public async Task Nothing_is_sent_before_eight_or_at_night_in_the_persons_own_time_zone()
    {
        var person = await NewPerson("Pia");
        await Subscribe(person);
        await Switches(person, low: true, peak: false);
        await Bag(person, await Bean(person, "Guji"), 60m);

        await Run(DateTimeOffset.Parse("2026-11-11T06:30:00Z")); // 07:30 in Amsterdam
        await Run(DateTimeOffset.Parse("2026-11-11T20:30:00Z")); // 21:30
        Received(person).ShouldBeEmpty();

        await Run(DateTimeOffset.Parse("2026-11-11T07:00:00Z")); // 08:00
        Received(person).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_same_bag_is_announced_once_not_every_day_and_not_again_after_a_second_run()
    {
        var person = await NewPerson("Pia");
        await Subscribe(person);
        await Switches(person, low: true, peak: false);
        await Bag(person, await Bean(person, "Guji"), 60m);

        await Run(Morning);
        await Run(Morning.AddHours(3)); // the job wakes every hour
        await Run(Morning.AddDays(1));
        await Run(Morning.AddDays(2));

        Received(person).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_new_low_bag_the_next_day_is_announced_by_itself()
    {
        var person = await NewPerson("Pia");
        await Subscribe(person);
        await Switches(person, low: true, peak: false);
        await Bag(person, await Bean(person, "Guji"), 60m);
        await Run(Morning);

        await Bag(person, await Bean(person, "Kiambu"), 30m);
        await Run(Morning.AddDays(1));

        Received(person).Select(p => p.Body).ShouldBe(["Guji is running low.", "Kiambu is running low."]);
    }

    [Fact]
    public async Task Someone_with_a_time_zone_far_away_gets_their_own_eight_oclock()
    {
        var person = await NewPerson("Aki");
        await Subscribe(person);
        await Switches(person, low: true, peak: false, zone: "Pacific/Auckland");
        await Bag(person, await Bean(person, "Guji"), 60m);

        await Run(DateTimeOffset.Parse("2026-11-10T19:30:00Z")); // 08:30 on 11 November in Auckland
        Received(person).ShouldHaveSingleItem();
    }

    // ---- Households ---------------------------------------------------------------------------

    [Fact]
    public async Task Every_member_of_the_household_with_the_switch_on_is_told_once()
    {
        var owner = await NewPerson("Olga");
        var member = await NewPerson("Joe");
        var quiet = await NewPerson("Quinn");
        using var invite = await owner.Client.PostAsJsonAsync("/households/mine/invites", new HouseholdInviteRequest(5, null), Ct);
        var code = (await invite.Content.ReadFromJsonAsync<InviteDto>(Ct))!.Code;
        foreach (var joiner in new[] { member, quiet })
        {
            (await joiner.Client.PostAsJsonAsync("/households/mine/join", new JoinRequest(code), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await Bag(owner, await Bean(owner, "Shared bean"), 50m);
        foreach (var person in new[] { owner, member, quiet })
        {
            await Subscribe(person);
        }

        await Switches(owner, low: true, peak: false);
        await Switches(member, low: true, peak: false);
        await Switches(quiet, low: false, peak: false);

        await Run(Morning);
        await Run(Morning.AddHours(1));

        Received(owner).ShouldHaveSingleItem();
        Received(member).ShouldHaveSingleItem();
        Received(quiet).ShouldBeEmpty();
    }

    // ---- Settings, content and failure --------------------------------------------------------

    [Fact]
    public async Task The_switches_start_off_and_are_saved()
    {
        var person = await NewPerson("Pia");
        var start = (await person.Client.GetFromJsonAsync<PushSettingsDto>("/push/settings", Ct))!;
        (start.LowStock, start.Peak).ShouldBe((false, false));

        await Switches(person, low: true, peak: true);

        var saved = (await person.Client.GetFromJsonAsync<PushSettingsDto>("/push/settings", Ct))!;
        (saved.LowStock, saved.Peak).ShouldBe((true, true));
    }

    [Fact]
    public async Task Nothing_is_sent_to_someone_who_never_switched_them_on()
    {
        var person = await NewPerson("Pia");
        await Subscribe(person);
        await Bag(person, await Bean(person, "Guji"), 60m, ageDays: 14);

        await Run(Morning);

        Received(person).ShouldBeEmpty();
    }

    [Fact]
    public async Task Settings_that_are_not_sensible_are_refused()
    {
        var person = await NewPerson("Pia");

        using var response = await person.Client.PutAsJsonAsync("/push/settings", new PushSettingsRequest(true, true, true, true, new InventorySettings(-5m, RestWindows.Default), "Europe/Amsterdam"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_unknown_time_zone_falls_back_to_UTC()
    {
        var person = await NewPerson("Pia");
        await Subscribe(person);
        await Switches(person, low: true, peak: false, zone: "Mars/Olympus");
        await Bag(person, await Bean(person, "Guji"), 60m);

        await Run(DateTimeOffset.Parse("2026-11-11T07:30:00Z")); // 07:30 UTC: not yet
        Received(person).ShouldBeEmpty();
        await Run(DateTimeOffset.Parse("2026-11-11T08:30:00Z"));
        Received(person).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_notification_holds_a_bean_name_or_a_count_and_nothing_else()
    {
        var person = await NewPerson("Pia");
        await Subscribe(person);
        await Switches(person, low: true, peak: false);
        await Bag(person, await Bean(person, "Guji"), 60m);

        await Run(Morning);

        var json = System.Text.Json.JsonSerializer.Serialize(Received(person).Single());
        json.ShouldNotContain(person.User.Id);
        json.ShouldNotContain(person.User.Email!);
        System.Text.Json.JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).Order().ShouldBe(["Body", "Tag", "Title", "Url"]);
    }

    [Fact]
    public async Task A_push_service_that_is_down_never_stops_the_job_for_the_others()
    {
        var first = await NewPerson("Pia");
        var second = await NewPerson("Quinn");
        foreach (var person in new[] { first, second })
        {
            await Subscribe(person);
            await Switches(person, low: true, peak: false);
            await Bag(person, await Bean(person, "Guji"), 60m);
        }

        app.Sender.Throw = true;
        try
        {
            await Run(Morning); // must not throw
        }
        finally
        {
            app.Sender.Throw = false;
        }
    }

    [Fact]
    public async Task A_device_the_push_service_says_is_gone_is_removed()
    {
        var person = await NewPerson("Pia");
        await Subscribe(person);
        await Switches(person, low: true, peak: false);
        await Bag(person, await Bean(person, "Guji"), 60m);
        app.Sender.Outcome = PushOutcome.Gone;
        try
        {
            await Run(Morning);
        }
        finally
        {
            app.Sender.Outcome = PushOutcome.Sent;
        }

        (await person.Client.GetFromJsonAsync<PushSettingsDto>("/push/settings", Ct))!.Devices.ShouldBe(0);
    }
}
