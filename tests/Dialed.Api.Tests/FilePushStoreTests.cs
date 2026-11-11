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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dialed.Api.Tests;

/// <summary>The push store that keeps its data in a file, so a restart forgets nothing (#259).</summary>
public sealed class FilePushStoreTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string folder = Path.Combine(Path.GetTempPath(), "dialed-push-" + Guid.NewGuid().ToString("N"));

    private string File_ => Path.Combine(folder, "push.json");

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private FilePushStore Open() => new(File_, NullLogger<FilePushStore>.Instance);

    private static PushSubscription Device(string user, string endpoint = "https://push.example.com/a") =>
        new(user, endpoint, "p256dh", "auth", "nl", DateTimeOffset.Parse("2026-11-11T08:00:00Z"));

    [Fact]
    public async Task Devices_switches_and_stock_alert_memory_survive_a_restart()
    {
        var first = Open();
        await first.SaveAsync(Device("u1"), Ct);
        await first.SaveSettingsAsync("u1", new PushSettings(false, true, true, true, new InventorySettings(150m, RestWindows.Default), "Europe/Amsterdam"), Ct);
        await first.SaveAlertStateAsync("u1", new StockAlertState(new DateOnly(2026, 11, 11), new StockAlertMemory(new HashSet<string> { "h1/bag1" }, new HashSet<string> { "h1/bag2" }) { PeakSoon = new HashSet<string> { "h1/bag3" } }), Ct);

        var second = Open();

        (await second.ListAsync("u1", Ct)).ShouldHaveSingleItem().ShouldBe(Device("u1"));
        var settings = await second.GetSettingsAsync("u1", Ct);
        (settings.Follows, settings.Household, settings.LowStock, settings.Peak, settings.TimeZone).ShouldBe((false, true, true, true, "Europe/Amsterdam"));
        settings.Inventory!.LowStockG.ShouldBe(150m);
        var state = await second.GetAlertStateAsync("u1", Ct);
        state.LastHandled.ShouldBe(new DateOnly(2026, 11, 11));
        state.Memory.Low.ShouldBe(["h1/bag1"]);
        state.Memory.Peak.ShouldBe(["h1/bag2"]);
        state.Memory.PeakSoon.ShouldBe(["h1/bag3"]);
        (await second.SubscribedUserIdsAsync(Ct)).ShouldBe(["u1"]);
    }

    [Fact]
    public async Task Removals_are_remembered_too()
    {
        var first = Open();
        await first.SaveAsync(Device("u1", "https://push.example.com/a"), Ct);
        await first.SaveAsync(Device("u2", "https://push.example.com/b"), Ct);
        await first.RemoveAsync("u1", "https://push.example.com/a", Ct);
        await first.RemoveUserAsync("u2", Ct);

        var second = Open();

        (await second.SubscribedUserIdsAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_a_file_the_store_starts_empty_and_makes_the_folder_on_the_first_change()
    {
        var store = Open();

        Directory.Exists(folder).ShouldBeFalse();
        await store.SaveAsync(Device("u1"), Ct);
        System.IO.File.Exists(File_).ShouldBeTrue();
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_starts_an_empty_store_instead_of_stopping_the_api()
    {
        Directory.CreateDirectory(folder);
        await System.IO.File.WriteAllTextAsync(File_, "{ this is not json", Ct);

        var store = Open();

        (await store.SubscribedUserIdsAsync(Ct)).ShouldBeEmpty();
        await store.SaveAsync(Device("u1"), Ct); // and the next change rewrites it
        (await Open().SubscribedUserIdsAsync(Ct)).ShouldBe(["u1"]);
    }

    [Fact]
    public async Task The_file_is_readable_by_its_owner_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await Open().SaveAsync(Device("u1"), Ct);

        System.IO.File.GetUnixFileMode(File_).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public async Task No_temporary_file_is_left_behind()
    {
        var store = Open();
        await store.SaveAsync(Device("u1"), Ct);
        await store.SaveAsync(Device("u1", "https://push.example.com/b"), Ct);

        Directory.GetFiles(folder).Select(Path.GetFileName).ShouldBe(["push.json"]);
    }

    // ---- Through the whole API: a restart does not repeat a stock alert -----------------------

    private sealed class FileApp(string path) : WebApplicationFactory<Program>
    {
        public RecordingPushSender Sender { get; } = new();

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("Push:StorePath", path);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPushSender>();
                services.AddSingleton<IPushSender>(Sender);
            });
        }
    }

    [Fact]
    public async Task A_restart_does_not_send_the_same_stock_alert_again()
    {
        var id = Guid.NewGuid().ToString("N");
        var user = new AppUser(id, $"{id}@example.com", "Pia", HouseholdService.PersonalHouseholdId(id));
        var morning = DateTimeOffset.Parse("2026-11-11T07:30:00Z");

        // Each factory below is a fresh server (its own user directory), so Pia is added again after the "restart".
        async Task<(FileApp Host, HttpClient Client)> Start()
        {
            var host = new FileApp(File_);
            await host.Services.GetRequiredService<IUserDirectory>().AddAsync(user, Ct);
            var tokens = await host.Services.GetRequiredService<RefreshTokenService>().SignInAsync(user, Ct);
            var client = host.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            return (host, client);
        }

        var (first, client1) = await Start();
        await client1.PutAsJsonAsync("/push/subscription", new PushSubscribeRequest("https://push.example.com/send/pia", "BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA_0QTpQtUbVlUls0VJXg7A8u-Ts1XbjhazAkj7I99e8QcYP7DkM", "tBHItJI5svbpez7KI4CCXg", "en"), Ct);
        await client1.PutAsJsonAsync("/push/settings", new PushSettingsRequest(true, true, true, false, null, "Europe/Amsterdam"), Ct);
        var beanId = Guid.CreateVersion7().ToString();
        await client1.PutAsJsonAsync($"/sync/beans/{beanId}", new JsonObject { ["id"] = beanId, ["name"] = "Guji", ["deleted"] = false, ["tastingNotes"] = new JsonArray(), ["varieties"] = new JsonArray(), ["photoIds"] = new JsonArray(), ["origin"] = new JsonObject() }, Ct);
        var bagId = Guid.CreateVersion7().ToString();
        await client1.PutAsJsonAsync($"/sync/bags/{bagId}", new JsonObject { ["id"] = bagId, ["beanId"] = beanId, ["weightG"] = 40, ["status"] = 1, ["deleted"] = false }, Ct);
        await first.Services.GetRequiredService<StockAlertService>().RunAsync(morning, Ct);
        first.Sender.Sent.Count.ShouldBe(1);
        client1.Dispose();
        await first.DisposeAsync();

        // The "restart": a new server over the same file. Devices and switches are back, and the day is already handled.
        var (second, client2) = await Start();
        (await client2.GetFromJsonAsync<PushSettingsDto>("/push/settings", Ct))!.Devices.ShouldBe(1);
        // The sync store is in memory too, so the bag is put back before the job runs.
        await client2.PutAsJsonAsync($"/sync/beans/{beanId}", new JsonObject { ["id"] = beanId, ["name"] = "Guji", ["deleted"] = false, ["tastingNotes"] = new JsonArray(), ["varieties"] = new JsonArray(), ["photoIds"] = new JsonArray(), ["origin"] = new JsonObject() }, Ct);
        await client2.PutAsJsonAsync($"/sync/bags/{bagId}", new JsonObject { ["id"] = bagId, ["beanId"] = beanId, ["weightG"] = 40, ["status"] = 1, ["deleted"] = false }, Ct);
        await second.Services.GetRequiredService<StockAlertService>().RunAsync(morning.AddHours(1), Ct);

        second.Sender.Sent.ShouldBeEmpty();
        client2.Dispose();
        await second.DisposeAsync();
    }
}
