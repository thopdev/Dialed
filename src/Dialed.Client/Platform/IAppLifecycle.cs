namespace Dialed.Client.Platform;

/// <summary>When the app goes to the background and comes back (sync, timers, wake lock). Web: page visibility.</summary>
public interface IAppLifecycle
{
    bool IsForeground { get; }

    /// <summary>The app came back to the foreground.</summary>
    event Action? Resumed;

    /// <summary>The app went to the background; it may be suspended soon.</summary>
    event Action? Paused;
}
