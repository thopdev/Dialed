namespace Dialed.Api.Push;

/// <summary>One device's push subscription as the browser's PushManager gave it (RFC 8030 endpoint, RFC 8291 keys).</summary>
public sealed record PushSubscription(string UserId, string Endpoint, string P256dh, string Auth, string Language, DateTimeOffset CreatedAt);

/// <summary>What a person wants to hear about. Everything is on until they switch it off.</summary>
public sealed record PushSettings(bool Follows = true, bool Household = true);

/// <summary>What the person sees: a title, one line, and where a tap goes. Never more than a name and the event.</summary>
public sealed record PushPayload(string Title, string Body, string Url, string Tag);

public enum PushOutcome
{
    Sent,

    /// <summary>The push service says the subscription no longer exists (404 or 410): remove it.</summary>
    Gone,

    Failed,
}

public enum PushKind
{
    FollowRequest,
    FollowAccepted,
    HouseholdJoined,
}

public interface IPushSender
{
    Task<PushOutcome> SendAsync(PushSubscription subscription, PushPayload payload, CancellationToken cancellationToken = default);
}

/// <summary>Subscriptions and switches per person. Cosmos DB takes over with the other server stores (#260).</summary>
public interface IPushStore
{
    /// <summary>Saves the device. An endpoint belongs to one person at a time: signing in as someone else on the same device moves it.</summary>
    Task SaveAsync(PushSubscription subscription, CancellationToken cancellationToken = default);

    /// <summary>Removes the device when it is this person's. <c>false</c> when it is not theirs or unknown.</summary>
    Task<bool> RemoveAsync(string userId, string endpoint, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PushSubscription>> ListAsync(string userId, CancellationToken cancellationToken = default);

    Task<PushSettings> GetSettingsAsync(string userId, CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(string userId, PushSettings settings, CancellationToken cancellationToken = default);

    Task RemoveUserAsync(string userId, CancellationToken cancellationToken = default);
}

public sealed class InMemoryPushStore : IPushStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, PushSubscription> byEndpoint = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PushSettings> settings = new(StringComparer.Ordinal);

    public Task SaveAsync(PushSubscription subscription, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            byEndpoint[subscription.Endpoint] = subscription;
        }

        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string userId, string endpoint, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(byEndpoint.TryGetValue(endpoint, out var found) && found.UserId == userId && byEndpoint.Remove(endpoint));
        }
    }

    public Task<IReadOnlyList<PushSubscription>> ListAsync(string userId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<PushSubscription>>([.. byEndpoint.Values.Where(s => s.UserId == userId).OrderBy(s => s.CreatedAt)]);
        }
    }

    public Task<PushSettings> GetSettingsAsync(string userId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(settings.GetValueOrDefault(userId) ?? new PushSettings());
        }
    }

    public Task SaveSettingsAsync(string userId, PushSettings value, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            settings[userId] = value;
        }

        return Task.CompletedTask;
    }

    public Task RemoveUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            foreach (var endpoint in byEndpoint.Where(e => e.Value.UserId == userId).Select(e => e.Key).ToList())
            {
                byEndpoint.Remove(endpoint);
            }

            settings.Remove(userId);
        }

        return Task.CompletedTask;
    }
}
