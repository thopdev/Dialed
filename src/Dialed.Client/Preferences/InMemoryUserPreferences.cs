using Dialed.Domain.Inventory;
using Dialed.Domain.Units;

namespace Dialed.Client.Preferences;

/// <summary>Preferences kept for the lifetime of the app session; replaced by the synced profile when the local database exists.</summary>
public sealed class InMemoryUserPreferences : IUserPreferences
{
    public UnitSystem Units { get; private set; } = UnitSystem.Metric;

    public InventorySettings Inventory { get; private set; } = InventorySettings.Default;

    public UserProfile Profile { get; private set; } = UserProfile.Default;

    public event Action? Changed;

    public Task SetUnitsAsync(UnitSystem units)
    {
        if (units != Units)
        {
            Units = units;
            Changed?.Invoke();
        }

        return Task.CompletedTask;
    }

    public Task SetInventoryAsync(InventorySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings != Inventory)
        {
            Inventory = settings;
            Changed?.Invoke();
        }

        return Task.CompletedTask;
    }

    public Task SetProfileAsync(UserProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile != Profile)
        {
            Profile = profile;
            Changed?.Invoke();
        }

        return Task.CompletedTask;
    }
}
