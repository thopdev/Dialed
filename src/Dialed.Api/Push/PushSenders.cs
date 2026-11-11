using System.Net;
using System.Text.Json;
using Dialed.Telemetry;
using Microsoft.Extensions.Options;

namespace Dialed.Api.Push;

/// <summary>Keys for VAPID (RFC 8292). In Azure they come from Key Vault (#260); in development they are generated when the app starts.</summary>
public sealed class PushOptions
{
    public const string Section = "Push";

    public string? PublicKey { get; set; }

    public string? PrivateKey { get; set; }

    /// <summary>A <c>mailto:</c> or <c>https:</c> address the push services can reach if we misbehave.</summary>
    public string Subject { get; set; } = "mailto:push@dialed.invalid";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(PublicKey) && !string.IsNullOrWhiteSpace(PrivateKey);
}

/// <summary>Sends with the maintained <c>WebPush</c> library: it signs the VAPID header and encrypts the payload.</summary>
public sealed class WebPushSender(IOptions<PushOptions> options, ILogger<WebPushSender> log) : IPushSender, IDisposable
{
    private readonly WebPush.WebPushClient client = new();

    public async Task<PushOutcome> SendAsync(PushSubscription subscription, PushPayload payload, CancellationToken cancellationToken = default)
    {
        var keys = options.Value;
        var vapid = new WebPush.VapidDetails(keys.Subject, keys.PublicKey, keys.PrivateKey);
        var target = new WebPush.PushSubscription(subscription.Endpoint, subscription.P256dh, subscription.Auth);
        try
        {
            await client.SendNotificationAsync(target, JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)), vapid, cancellationToken);
            return PushOutcome.Sent;
        }
        catch (WebPush.WebPushException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return PushOutcome.Gone;
        }
        catch (Exception e) when (e is WebPush.WebPushException or HttpRequestException or TaskCanceledException)
        {
            // The status code is enough for the log: never the endpoint, which identifies a device.
            log.LogWarning("A push could not be delivered ({Status}).", (e as WebPush.WebPushException)?.StatusCode.ToString() ?? e.GetType().Name);
            return PushOutcome.Failed;
        }
    }

    public void Dispose() => client.Dispose();
}

/// <summary>Used when there are no keys: nothing is sent, and the app says push is not available.</summary>
public sealed class NoPushSender : IPushSender
{
    public Task<PushOutcome> SendAsync(PushSubscription subscription, PushPayload payload, CancellationToken cancellationToken = default) => Task.FromResult(PushOutcome.Failed);
}
