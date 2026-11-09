using Dialed.Client.Platform;
using Dialed.Client.Preferences;
using Dialed.Domain.Inventory;
using Dialed.Domain.Model;
using Dialed.Domain.Units;

namespace Dialed.Client.Tests;

/// <summary>Preferences and finished setup survive a restart (#226).</summary>
public sealed class StoredUserPreferencesTests
{
    [Fact]
    public async Task A_new_launch_reads_back_what_the_last_one_saved()
    {
        var store = new InMemoryPreferenceStore();
        var first = new StoredUserPreferences(store);
        await first.SetUnitsAsync(UnitSystem.Imperial);
        await first.SetInventoryAsync(new InventorySettings(80m, RestWindows.Default with { Light = new RestWindow(12, 35) }));
        await first.SetProfileAsync(UserProfile.Default with { DisplayName = "Thomas", OnboardingDone = true, FavouriteMethods = ["v60", "espresso"], DefaultVisibility = Visibility.Friends });

        var second = new StoredUserPreferences(store);
        await second.LoadAsync();

        second.Units.ShouldBe(UnitSystem.Imperial);
        second.Inventory.LowStockG.ShouldBe(80m);
        second.Inventory.RestWindows.Light.ShouldBe(new RestWindow(12, 35));
        second.Profile.OnboardingDone.ShouldBeTrue();
        second.Profile.DisplayName.ShouldBe("Thomas");
        second.Profile.FavouriteMethods.ShouldBe(["v60", "espresso"]);
        second.Profile.DefaultVisibility.ShouldBe(Visibility.Friends);
    }

    [Fact]
    public async Task Nothing_saved_gives_the_defaults()
    {
        var preferences = new StoredUserPreferences(new InMemoryPreferenceStore());

        await preferences.LoadAsync();

        preferences.Profile.ShouldBe(UserProfile.Default);
        preferences.Units.ShouldBe(UnitSystem.Metric);
        preferences.Inventory.ShouldBe(InventorySettings.Default);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"units\":\"Furlongs\"}")]
    [InlineData("[]")]
    public async Task A_damaged_value_gives_the_defaults(string json)
    {
        var store = new InMemoryPreferenceStore();
        await store.SaveAsync(json);
        var preferences = new StoredUserPreferences(store);

        await preferences.LoadAsync();

        preferences.Profile.OnboardingDone.ShouldBeFalse();
        preferences.Units.ShouldBe(UnitSystem.Metric);
    }

    [Fact]
    public async Task A_failing_store_never_stops_a_change()
    {
        var preferences = new StoredUserPreferences(new BrokenStore());
        var changed = 0;
        preferences.Changed += () => changed++;

        await preferences.LoadAsync();
        await preferences.SetProfileAsync(UserProfile.Default with { OnboardingDone = true });

        preferences.Profile.OnboardingDone.ShouldBeTrue();
        changed.ShouldBe(1);
    }

    [Fact]
    public async Task Setting_the_same_value_does_not_save_or_notify()
    {
        var store = new InMemoryPreferenceStore();
        var preferences = new StoredUserPreferences(store);
        var changed = 0;
        preferences.Changed += () => changed++;

        await preferences.SetUnitsAsync(UnitSystem.Metric);

        changed.ShouldBe(0);
        store.Json.ShouldBeNull();
    }

    private sealed class BrokenStore : IPreferenceStore
    {
        public Task<string?> LoadAsync() => throw new InvalidOperationException("Storage is blocked.");

        public Task SaveAsync(string json) => throw new IOException("Storage is full.");
    }
}
