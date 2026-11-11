using System.Globalization;
using Dialed.Client.Account;
using Dialed.Client.Platform;

namespace Dialed.Client.Push;

public enum PushAvailability
{
    /// <summary>Nobody is signed in: pushes belong to an account.</summary>
    SignedOut,

    /// <summary>This device or browser cannot receive pushes.</summary>
    Unsupported,

    /// <summary>iPhone or iPad in Safari: add the app to the Home Screen first.</summary>
    NotInstalled,

    /// <summary>The server has no push keys (or cannot be reached).</summary>
    ServerUnavailable,

    /// <summary>The person said no in the browser; only the browser settings can change that.</summary>
    Denied,

    /// <summary>Can be turned on with a tap.</summary>
    Off,

    /// <summary>This device is registered.</summary>
    On,
}

public sealed record PushState(PushAvailability Availability, bool Follows = true, bool Household = true, bool LowStock = false, bool Peak = false);

/// <summary>
/// Turning notifications on and off for this device (#100) and the per-type switches. Each device registers its own subscription;
/// nothing here ever fails the caller beyond a state to show.
/// </summary>
public sealed class PushNotificationService(IPushNotifications platform, IPushApi api, AuthSession session, Preferences.IUserPreferences? preferences = null)
{
    public async Task<PushState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        if (!session.IsSignedIn)
        {
            return new PushState(PushAvailability.SignedOut);
        }

        switch (await platform.GetSupportAsync(cancellationToken))
        {
            case PushSupport.Unsupported:
                return new PushState(PushAvailability.Unsupported);
            case PushSupport.NotInstalled:
                return new PushState(PushAvailability.NotInstalled);
        }

        var permission = await platform.GetPermissionAsync(cancellationToken);
        if (permission == PushPermission.Denied)
        {
            return new PushState(PushAvailability.Denied);
        }

        try
        {
            var settings = await api.GetSettingsAsync(cancellationToken);
            var subscribed = permission == PushPermission.Granted && await platform.GetSubscriptionAsync(cancellationToken) is not null;
            return new PushState(subscribed ? PushAvailability.On : PushAvailability.Off, settings.Follows, settings.Household, settings.LowStock, settings.Peak);
        }
        catch (Exception e) when (e is AccountApiException or AccountRefusedException)
        {
            return new PushState(PushAvailability.ServerUnavailable);
        }
    }

    /// <summary>Asks for permission, subscribes this device and registers it. Call it from a tap.</summary>
    public async Task<PushState> EnableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var key = await api.GetKeyAsync(cancellationToken);
            if (key is null)
            {
                return new PushState(PushAvailability.ServerUnavailable);
            }

            var result = await platform.SubscribeAsync(key, cancellationToken);
            if (result.Device is null)
            {
                return result.Permission == PushPermission.Denied ? new PushState(PushAvailability.Denied) : await GetStateAsync(cancellationToken);
            }

            await api.SubscribeAsync(result.Device, Language(), cancellationToken);
        }
        catch (Exception e) when (e is AccountApiException or AccountRefusedException)
        {
            return new PushState(PushAvailability.ServerUnavailable);
        }

        return await GetStateAsync(cancellationToken);
    }

    /// <summary>The app language (<c>nl</c> or <c>en</c>); the server stores it so the notification comes in that language.</summary>
    private static string Language() => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "nl" ? "nl" : "en";

    /// <summary>
    /// After the inventory settings (low-stock threshold, rest windows) change: tells the server, so its alerts keep agreeing with the app.
    /// Only matters while a stock alert is on; best effort, never throws.
    /// </summary>
    public async Task SyncStockSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (!session.IsSignedIn)
        {
            return;
        }

        try
        {
            var current = await api.GetSettingsAsync(cancellationToken);
            if (current.LowStock || current.Peak)
            {
                await api.SaveSettingsAsync(current.Follows, current.Household, current.LowStock, current.Peak, preferences?.Inventory, await platform.GetTimeZoneAsync(cancellationToken), cancellationToken);
            }
        }
        catch (Exception e) when (e is AccountApiException or AccountRefusedException)
        {
            // Offline: the server keeps the earlier numbers until the next change.
        }
    }

    /// <summary>Removes this device at the push service and at the server. The server is told first, so a failure there leaves nothing behind.</summary>
    public async Task<PushState> DisableAsync(CancellationToken cancellationToken = default)
    {
        await ForgetThisDeviceAsync(cancellationToken);
        return await GetStateAsync(cancellationToken);
    }

    /// <summary>For signing out: best effort, never throws.</summary>
    public async Task ForgetThisDeviceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (await platform.GetSubscriptionAsync(cancellationToken) is { } device)
            {
                try
                {
                    await api.UnsubscribeAsync(device.Endpoint, cancellationToken);
                }
                catch (Exception e) when (e is AccountApiException or AccountRefusedException)
                {
                    // Offline: the push service answers 410 once the browser drops the subscription, and the server removes it then.
                }
            }

            await platform.UnsubscribeAsync(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A browser that cannot unsubscribe is no reason to stop signing out.
        }
    }

    public async Task<PushState> SaveSwitchesAsync(bool follows, bool household, bool lowStock = false, bool peak = false, CancellationToken cancellationToken = default)
    {
        try
        {
            await api.SaveSettingsAsync(follows, household, lowStock, peak, preferences?.Inventory, await platform.GetTimeZoneAsync(cancellationToken), cancellationToken);
        }
        catch (Exception e) when (e is AccountApiException or AccountRefusedException)
        {
            return new PushState(PushAvailability.ServerUnavailable);
        }

        return await GetStateAsync(cancellationToken);
    }
}
