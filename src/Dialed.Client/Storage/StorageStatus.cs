namespace Dialed.Client.Storage;

/// <summary>What the host could do about keeping data on this device; shown in Settings.</summary>
public sealed class StorageStatus
{
    /// <summary>The local database was opened, so data survives closing the app. <c>false</c>: memory only.</summary>
    public bool Durable { get; set; }

    /// <summary>The browser agreed not to clear the data when space runs low (iOS may still clear it after weeks unused).</summary>
    public bool Persisted { get; set; }

    /// <summary>The last write to the local database failed (storage full or blocked): the change is kept in memory and, when signed in, still sent to the account.</summary>
    public bool WriteFailed { get; private set; }

    /// <summary>Raised when <see cref="WriteFailed"/> changes.</summary>
    public event Action? Changed;

    public void ReportWriteFailed()
    {
        if (!WriteFailed)
        {
            WriteFailed = true;
            Changed?.Invoke();
        }
    }

    public void ReportWriteSucceeded()
    {
        if (WriteFailed)
        {
            WriteFailed = false;
            Changed?.Invoke();
        }
    }
}
