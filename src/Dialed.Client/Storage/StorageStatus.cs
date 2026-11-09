namespace Dialed.Client.Storage;

/// <summary>What the host could do about keeping data on this device; shown in Settings.</summary>
public sealed class StorageStatus
{
    /// <summary>The local database was opened, so data survives closing the app. <c>false</c>: memory only.</summary>
    public bool Durable { get; set; }

    /// <summary>The browser agreed not to clear the data when space runs low (iOS may still clear it after weeks unused).</summary>
    public bool Persisted { get; set; }
}
