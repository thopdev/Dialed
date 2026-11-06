namespace Dialed.Client;

/// <summary>
/// How the app is installed and updated. The browser host implements this with a service worker;
/// a MAUI host never has updates to offer (the app store delivers them) and is always "installed".
/// </summary>
public interface IPwaState
{
    /// <summary>A newer version has been downloaded and waits for the user to switch to it.</summary>
    bool UpdateAvailable { get; }

    /// <summary>Running as an installed app (home screen icon) rather than in a browser tab.</summary>
    bool IsInstalled { get; }

    /// <summary>
    /// Safari on iPhone/iPad in a tab. Only here is the manual "Share, then Add to Home Screen" guide useful:
    /// iOS offers no install prompt.
    /// </summary>
    bool NeedsInstallGuide { get; }

    /// <summary>Raised when <see cref="UpdateAvailable"/> changes.</summary>
    event Action? Changed;

    /// <summary>Starts the new version (reloads the app).</summary>
    Task ApplyUpdateAsync();
}
