namespace Dialed.Client.Platform;

public sealed record StorageEstimate(long UsageBytes, long QuotaBytes);

/// <summary>
/// Asks the platform not to clear local data when it is short on space (the local database holds
/// brews that are not synced yet). Web: StorageManager.persist(); MAUI: always persistent.
/// </summary>
public interface IStoragePersistence
{
    Task<bool> IsPersistedAsync();

    /// <summary>Asks for persistent storage; returns whether it was granted.</summary>
    Task<bool> RequestPersistenceAsync();

    Task<StorageEstimate?> EstimateAsync();
}
