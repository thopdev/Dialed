using Dialed.Domain.Units;

namespace Dialed.Client.Preferences;

/// <summary>Preferences kept for the lifetime of the app session; replaced by the synced profile when the local database exists.</summary>
public sealed class InMemoryUserPreferences : IUserPreferences
{
    public UnitSystem Units { get; private set; } = UnitSystem.Metric;

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
}
