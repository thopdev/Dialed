namespace Dialed.Client.Platform;

/// <summary>Keeps the screen on while brewing with the timer. Web: Screen Wake Lock API.</summary>
public interface IWakeLock
{
    bool IsHeld { get; }

    /// <summary>Returns <c>false</c> when the platform refuses (unsupported, low battery).</summary>
    Task<bool> AcquireAsync();

    Task ReleaseAsync();
}
