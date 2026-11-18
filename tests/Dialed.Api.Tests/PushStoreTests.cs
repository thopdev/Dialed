using Dialed.Api.Push;
using Dialed.Domain.Inventory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dialed.Api.Tests;

/// <summary>What every push store does: devices, switches, stock alert memory and the one-push-per-event claims (#100, #259, #260).</summary>
public abstract class PushStoreContract
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected static readonly DateTimeOffset Start = new(2026, 11, 18, 8, 0, 0, TimeSpan.Zero);

    protected abstract IPushStore NewStore();

    protected static string NewUser() => $"user-{Guid.NewGuid():N}";

    protected static PushSubscription Device(string user, string? endpoint = null, DateTimeOffset? at = null) =>
        new(user, endpoint ?? $"https://push.example.com/{Guid.NewGuid():N}?a=1&b=/x", "p256dh", "auth", "nl", at ?? Start);

    [Fact]
    public async Task Devices_are_listed_oldest_first_and_removed_by_their_owner_only()
    {
        var store = NewStore();
        var user = NewUser();
        var older = Device(user, at: Start);
        var newer = Device(user, at: Start.AddMinutes(5));
        await store.SaveAsync(newer, Ct);
        await store.SaveAsync(older, Ct);

        (await store.ListAsync(user, Ct)).ShouldBe([older, newer]);
        (await store.RemoveAsync(NewUser(), older.Endpoint, Ct)).ShouldBeFalse();
        (await store.RemoveAsync(user, older.Endpoint, Ct)).ShouldBeTrue();
        (await store.RemoveAsync(user, older.Endpoint, Ct)).ShouldBeFalse();
        (await store.ListAsync(user, Ct)).ShouldBe([newer]);
    }

    [Fact]
    public async Task Saving_a_device_again_updates_it()
    {
        var store = NewStore();
        var user = NewUser();
        var device = Device(user);
        await store.SaveAsync(device, Ct);

        await store.SaveAsync(device with { Language = "en", Auth = "auth2" }, Ct);

        (await store.ListAsync(user, Ct)).ShouldHaveSingleItem().ShouldBe(device with { Language = "en", Auth = "auth2" });
    }

    [Fact]
    public async Task A_device_moves_to_whoever_signs_in_on_it()
    {
        var store = NewStore();
        var (first, second) = (NewUser(), NewUser());
        var device = Device(first);
        await store.SaveAsync(device, Ct);

        await store.SaveAsync(device with { UserId = second }, Ct);

        (await store.ListAsync(first, Ct)).ShouldBeEmpty();
        (await store.ListAsync(second, Ct)).ShouldHaveSingleItem().UserId.ShouldBe(second);
    }

    [Fact]
    public async Task Settings_default_until_saved()
    {
        var store = NewStore();
        var user = NewUser();
        (await store.GetSettingsAsync(user, Ct)).ShouldBe(new PushSettings());

        var settings = new PushSettings(Follows: false, Household: true, LowStock: true, Peak: true, InventorySettings.Default, "Europe/Amsterdam", Service: true);
        await store.SaveSettingsAsync(user, settings, Ct);

        (await store.GetSettingsAsync(user, Ct)).ShouldBe(settings);
    }

    [Fact]
    public async Task Stock_alert_memory_is_kept_per_person()
    {
        var store = NewStore();
        var user = NewUser();
        (await store.GetAlertStateAsync(user, Ct)).ShouldBe(StockAlertState.None);

        var memory = new StockAlertMemory(new HashSet<string> { "h/low" }, new HashSet<string> { "h/peak" }) { PeakSoon = new HashSet<string> { "h/soon" }, Gear = new HashSet<string> { "h/g/2026-11-01" } };
        await store.SaveAlertStateAsync(user, new StockAlertState(new DateOnly(2026, 11, 18), memory), Ct);

        var read = await store.GetAlertStateAsync(user, Ct);
        read.LastHandled.ShouldBe(new DateOnly(2026, 11, 18));
        read.Memory.Low.ShouldBe(["h/low"]);
        read.Memory.Peak.ShouldBe(["h/peak"]);
        read.Memory.PeakSoon.ShouldBe(["h/soon"]);
        read.Memory.Gear.ShouldBe(["h/g/2026-11-01"]);
        (await store.GetAlertStateAsync(NewUser(), Ct)).ShouldBe(StockAlertState.None);
    }

    [Fact]
    public async Task Subscribed_users_are_those_with_a_device()
    {
        var store = NewStore();
        var (a, b, c) = (NewUser(), NewUser(), NewUser());
        await store.SaveAsync(Device(a), Ct);
        await store.SaveAsync(Device(a), Ct);
        await store.SaveAsync(Device(b), Ct);
        await store.SaveSettingsAsync(c, new PushSettings(), Ct);

        var users = await store.SubscribedUserIdsAsync(Ct);

        users.ShouldContain(a);
        users.ShouldContain(b);
        users.ShouldNotContain(c);
        users.Count(u => u == a).ShouldBe(1);
    }

    [Fact]
    public async Task Removing_a_person_removes_everything_of_theirs()
    {
        var store = NewStore();
        var (user, other) = (NewUser(), NewUser());
        await store.SaveAsync(Device(user), Ct);
        await store.SaveAsync(Device(other), Ct);
        await store.SaveSettingsAsync(user, new PushSettings(Follows: false), Ct);
        await store.SaveAlertStateAsync(user, new StockAlertState(new DateOnly(2026, 11, 18), StockAlertMemory.Empty), Ct);
        await store.TryClaimAsync(user, "FollowRequest|x", Start, TimeSpan.FromHours(1), Ct);

        await store.RemoveUserAsync(user, Ct);

        (await store.ListAsync(user, Ct)).ShouldBeEmpty();
        (await store.GetSettingsAsync(user, Ct)).ShouldBe(new PushSettings());
        (await store.GetAlertStateAsync(user, Ct)).ShouldBe(StockAlertState.None);
        (await store.SubscribedUserIdsAsync(Ct)).ShouldNotContain(user);
        (await store.ListAsync(other, Ct)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task An_event_is_claimed_once_within_its_window()
    {
        var store = NewStore();
        var user = NewUser();
        var hour = TimeSpan.FromHours(1);

        (await store.TryClaimAsync(user, "FollowRequest|actor", Start, hour, Ct)).ShouldBeTrue();
        (await store.TryClaimAsync(user, "FollowRequest|actor", Start.AddMinutes(59), hour, Ct)).ShouldBeFalse();
        (await store.TryClaimAsync(user, "FollowAccepted|actor", Start, hour, Ct)).ShouldBeTrue();
        (await store.TryClaimAsync(NewUser(), "FollowRequest|actor", Start, hour, Ct)).ShouldBeTrue();
        (await store.TryClaimAsync(user, "FollowRequest|actor", Start.AddHours(1), hour, Ct)).ShouldBeTrue();
    }
}

public sealed class InMemoryPushStoreTests : PushStoreContract
{
    protected override IPushStore NewStore() => new InMemoryPushStore();
}

/// <summary>The Cosmos DB push store against a real Cosmos DB; skips without <c>COSMOS_CONNECTION_STRING</c>.</summary>
public sealed class CosmosPushStoreTests : PushStoreContract, IAsyncDisposable
{
    private readonly CosmosTestDatabase database = new();

    protected override IPushStore NewStore() => new CosmosPushStore(database.New());

    public ValueTask DisposeAsync() => database.DisposeAsync();

    [Fact]
    public async Task Devices_settings_and_alert_memory_hold_for_a_new_instance()
    {
        var user = NewUser();
        var device = Device(user);
        await NewStore().SaveAsync(device, Ct);
        await NewStore().SaveSettingsAsync(user, new PushSettings(LowStock: true), Ct);
        await NewStore().SaveAlertStateAsync(user, new StockAlertState(new DateOnly(2026, 11, 18), new StockAlertMemory(new HashSet<string> { "h/b" }, new HashSet<string>())), Ct);

        var restarted = NewStore();
        (await restarted.ListAsync(user, Ct)).ShouldBe([device]);
        (await restarted.GetSettingsAsync(user, Ct)).LowStock.ShouldBeTrue();
        (await restarted.GetAlertStateAsync(user, Ct)).Memory.Low.ShouldBe(["h/b"]);
    }

    [Fact]
    public async Task A_claim_holds_for_a_new_instance_and_racing_instances_claim_once()
    {
        var user = NewUser();
        (await NewStore().TryClaimAsync(user, "FollowRequest|a", Start, TimeSpan.FromHours(1), Ct)).ShouldBeTrue();
        (await NewStore().TryClaimAsync(user, "FollowRequest|a", Start.AddMinutes(1), TimeSpan.FromHours(1), Ct)).ShouldBeFalse();

        var stores = Enumerable.Range(0, 4).Select(_ => NewStore()).ToList();
        var results = await Task.WhenAll(stores.Select(s => s.TryClaimAsync(user, "HouseholdJoined|b", Start, TimeSpan.FromHours(1), Ct)));
        results.Count(r => r).ShouldBe(1);
    }
}

/// <summary>Which push store the API picks: Cosmos DB wins over the file, which is never used when Cosmos DB is configured (#260).</summary>
public sealed class PushStoreRegistrationTests
{
    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Dialed.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDialedPush(configuration, new Environment());
        return services.BuildServiceProvider();
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"dialed-push-{Guid.NewGuid():N}", "push.json");

    [Fact]
    public void Without_settings_the_store_is_in_memory()
    {
        using var provider = Build([]);
        provider.GetRequiredService<IPushStore>().ShouldBeOfType<InMemoryPushStore>();
    }

    [Fact]
    public void With_a_store_path_and_no_cosmos_the_store_is_the_file()
    {
        using var provider = Build(new() { ["Push:StorePath"] = TempFile() });
        provider.GetRequiredService<IPushStore>().ShouldBeOfType<FilePushStore>();
    }

    [Fact]
    public void With_cosmos_configured_the_file_is_never_used()
    {
        using var provider = Build(new() { ["Push:StorePath"] = TempFile(), ["ConnectionStrings:cosmos"] = CosmosTestConnection.Emulator });
        provider.GetRequiredService<IPushStore>().ShouldBeOfType<CosmosPushStore>();
    }
}
