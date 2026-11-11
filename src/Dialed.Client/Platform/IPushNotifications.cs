namespace Dialed.Client.Platform;

/// <summary>Whether this device can receive pushes at all.</summary>
public enum PushSupport
{
    Supported,

    /// <summary>iPhone and iPad only allow web push from an app added to the Home Screen (iOS 16.4 or later).</summary>
    NotInstalled,

    Unsupported,
}

public enum PushPermission
{
    /// <summary>Not asked yet.</summary>
    Default,

    Granted,

    Denied,
}

/// <summary>This device's address at the push service, as the browser reports it (RFC 8030 endpoint, RFC 8291 keys).</summary>
public sealed record PushDevice(string Endpoint, string P256dh, string Auth);

/// <summary>The answer to <see cref="IPushNotifications.SubscribeAsync"/>: the device when allowed, otherwise what the person chose.</summary>
public sealed record PushSubscribeResult(PushDevice? Device, PushPermission Permission);

/// <summary>
/// Receiving notifications on this device. Web: the service worker's <c>PushManager</c> and the Notification permission; the permission
/// prompt must come from a tap, so <see cref="SubscribeAsync"/> is only called from a button. A native host would use APNs (#261).
/// </summary>
public interface IPushNotifications
{
    Task<PushSupport> GetSupportAsync(CancellationToken cancellationToken = default);

    Task<PushPermission> GetPermissionAsync(CancellationToken cancellationToken = default);

    /// <summary>This device's subscription, or <c>null</c> when it has none.</summary>
    Task<PushDevice?> GetSubscriptionAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks for permission when it has not been given yet, then subscribes with the server's public VAPID key.</summary>
    Task<PushSubscribeResult> SubscribeAsync(string publicKey, CancellationToken cancellationToken = default);

    /// <summary>The device's IANA time zone (for example <c>Europe/Amsterdam</c>), so stock alerts arrive around 08:00 local time. <c>null</c> when unknown.</summary>
    Task<string?> GetTimeZoneAsync(CancellationToken cancellationToken = default);

    /// <summary>Drops this device's subscription at the push service. Returns its endpoint, or <c>null</c> when there was none.</summary>
    Task<string?> UnsubscribeAsync(CancellationToken cancellationToken = default);
}

/// <summary>The default where nothing can be pushed: tests and hosts that have not registered an implementation.</summary>
public sealed class UnsupportedPushNotifications : IPushNotifications
{
    public static readonly UnsupportedPushNotifications Instance = new();

    public Task<PushSupport> GetSupportAsync(CancellationToken cancellationToken = default) => Task.FromResult(PushSupport.Unsupported);

    public Task<PushPermission> GetPermissionAsync(CancellationToken cancellationToken = default) => Task.FromResult(PushPermission.Default);

    public Task<PushDevice?> GetSubscriptionAsync(CancellationToken cancellationToken = default) => Task.FromResult<PushDevice?>(null);

    public Task<PushSubscribeResult> SubscribeAsync(string publicKey, CancellationToken cancellationToken = default) => Task.FromResult(new PushSubscribeResult(null, PushPermission.Default));

    public Task<string?> UnsubscribeAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task<string?> GetTimeZoneAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
}
