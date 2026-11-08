using Dialed.Domain.Units;

namespace Dialed.Client.Preferences;

/// <summary>The signed-in user's display preferences. Today in memory; later part of the synced profile (#34).</summary>
public interface IUserPreferences
{
    UnitSystem Units { get; }

    event Action? Changed;

    Task SetUnitsAsync(UnitSystem units);
}
