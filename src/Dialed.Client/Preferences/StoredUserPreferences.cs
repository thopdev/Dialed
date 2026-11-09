using System.Text.Json;
using System.Text.Json.Serialization;
using Dialed.Client.Platform;
using Dialed.Domain.Inventory;
using Dialed.Domain.Units;

namespace Dialed.Client.Preferences;

/// <summary>
/// Preferences saved on the device (#226): read once at start-up, saved after every change. A damaged or missing value
/// gives the defaults, and storage errors never reach the screens. The synced profile (#34) replaces this later.
/// </summary>
public sealed class StoredUserPreferences(IPreferenceStore store) : IUserPreferences
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public UnitSystem Units { get; private set; } = UnitSystem.Metric;

    public InventorySettings Inventory { get; private set; } = InventorySettings.Default;

    public UserProfile Profile { get; private set; } = UserProfile.Default;

    public event Action? Changed;

    /// <summary>Reads the saved preferences. Call once before the first screen.</summary>
    public async Task LoadAsync()
    {
        try
        {
            if (await store.LoadAsync() is { Length: > 0 } json && JsonSerializer.Deserialize<Snapshot>(json, Json) is { } saved)
            {
                Units = saved.Units;
                Inventory = saved.Inventory ?? InventorySettings.Default;
                Profile = saved.Profile is { } profile ? profile with { FavouriteMethods = profile.FavouriteMethods ?? [] } : UserProfile.Default;
                Changed?.Invoke();
            }
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            // Damaged value: start from the defaults; the next change overwrites it.
        }
    }

    public Task SetUnitsAsync(UnitSystem units)
    {
        if (units == Units)
        {
            return Task.CompletedTask;
        }

        Units = units;
        return ChangedAsync();
    }

    public Task SetInventoryAsync(InventorySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings == Inventory)
        {
            return Task.CompletedTask;
        }

        Inventory = settings;
        return ChangedAsync();
    }

    public Task SetProfileAsync(UserProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile == Profile)
        {
            return Task.CompletedTask;
        }

        Profile = profile;
        return ChangedAsync();
    }

    private async Task ChangedAsync()
    {
        Changed?.Invoke();
        try
        {
            await store.SaveAsync(JsonSerializer.Serialize(new Snapshot(Units, Inventory, Profile), Json));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Storage blocked or full: the change still applies until the app closes.
        }
    }

    private sealed record Snapshot(UnitSystem Units, InventorySettings? Inventory, UserProfile? Profile);
}
