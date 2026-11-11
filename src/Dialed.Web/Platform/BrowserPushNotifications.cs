using Dialed.Client.Platform;
using Microsoft.JSInterop;

namespace Dialed.Web.Platform;

/// <summary>The service worker's <c>PushManager</c> and the Notification permission. See <c>push*</c> in platform.js.</summary>
internal sealed class BrowserPushNotifications(BrowserModule browser) : IPushNotifications
{
    private sealed record DeviceResult(PushDevice? Device, string Permission);

    public async Task<PushSupport> GetSupportAsync(CancellationToken cancellationToken = default)
    {
        var module = await browser.GetAsync();
        return await module.InvokeAsync<string>("pushSupport", cancellationToken) switch
        {
            "supported" => PushSupport.Supported,
            "notInstalled" => PushSupport.NotInstalled,
            _ => PushSupport.Unsupported,
        };
    }

    public async Task<PushPermission> GetPermissionAsync(CancellationToken cancellationToken = default)
    {
        var module = await browser.GetAsync();
        return ToPermission(await module.InvokeAsync<string>("pushPermission", cancellationToken));
    }

    public async Task<PushDevice?> GetSubscriptionAsync(CancellationToken cancellationToken = default)
    {
        var module = await browser.GetAsync();
        try
        {
            return await module.InvokeAsync<PushDevice?>("pushGetSubscription", cancellationToken);
        }
        catch (JSException)
        {
            return null;
        }
    }

    public async Task<PushSubscribeResult> SubscribeAsync(string publicKey, CancellationToken cancellationToken = default)
    {
        var module = await browser.GetAsync();
        try
        {
            var result = await module.InvokeAsync<DeviceResult>("pushSubscribe", cancellationToken, publicKey);
            return new PushSubscribeResult(result.Device, ToPermission(result.Permission));
        }
        catch (JSException)
        {
            // The push service refused (offline, blocked, bad key): the person sees "could not turn on", not an error page.
            return new PushSubscribeResult(null, PushPermission.Default);
        }
    }

    public async Task<string?> GetTimeZoneAsync(CancellationToken cancellationToken = default)
    {
        var module = await browser.GetAsync();
        try
        {
            return await module.InvokeAsync<string?>("pushTimeZone", cancellationToken);
        }
        catch (JSException)
        {
            return null;
        }
    }

    public async Task<string?> UnsubscribeAsync(CancellationToken cancellationToken = default)
    {
        var module = await browser.GetAsync();
        try
        {
            return await module.InvokeAsync<string?>("pushUnsubscribe", cancellationToken);
        }
        catch (JSException)
        {
            return null;
        }
    }

    private static PushPermission ToPermission(string value) => value switch
    {
        "granted" => PushPermission.Granted,
        "denied" => PushPermission.Denied,
        _ => PushPermission.Default,
    };
}
