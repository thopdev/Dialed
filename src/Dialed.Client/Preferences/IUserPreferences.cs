using Dialed.Domain.Inventory;
using Dialed.Domain.Units;

namespace Dialed.Client.Preferences;

/// <summary>The signed-in user's display preferences. Today in memory; later part of the synced profile (#34).</summary>
public interface IUserPreferences
{
    UnitSystem Units { get; }

    /// <summary>Low-stock threshold and rest/peak windows.</summary>
    InventorySettings Inventory { get; }

    UserProfile Profile { get; }

    event Action? Changed;

    Task SetUnitsAsync(UnitSystem units);

    Task SetInventoryAsync(InventorySettings settings);

    Task SetProfileAsync(UserProfile profile);
}
