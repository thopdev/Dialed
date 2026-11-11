using Dialed.Domain.Inventory;

namespace Dialed.Api.Push;

/// <summary>One device's push subscription as the browser's PushManager gave it (RFC 8030 endpoint, RFC 8291 keys).</summary>
public sealed record PushSubscription(string UserId, string Endpoint, string P256dh, string Auth, string Language, DateTimeOffset CreatedAt);

/// <summary>
/// What a person wants to hear about. Follow and household notifications are on until switched off; the stock alerts (#259) are off until
/// switched on. For those the app sends what the server needs to judge a bag the way the app does: the inventory settings and the time zone.
/// </summary>
public sealed record PushSettings(bool Follows = true, bool Household = true, bool LowStock = false, bool Peak = false, InventorySettings? Inventory = null, string? TimeZone = null);

/// <summary>What the stock alert job remembers about one person: the last local day they were looked at, and which bags they were told about.</summary>
public sealed record StockAlertState(DateOnly? LastHandled, StockAlertMemory Memory)
{
    public static StockAlertState None { get; } = new(null, StockAlertMemory.Empty);
}

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
    LowStock,
    Peak,
    PeakSoon,
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

    /// <summary>Everyone with at least one device (the stock alert job only looks at them).</summary>
    Task<IReadOnlyList<string>> SubscribedUserIdsAsync(CancellationToken cancellationToken = default);

    Task<StockAlertState> GetAlertStateAsync(string userId, CancellationToken cancellationToken = default);

    Task SaveAlertStateAsync(string userId, StockAlertState state, CancellationToken cancellationToken = default);
}

/// <summary>Everything a push store holds, as plain data (sets as arrays) so it can be written to a file.</summary>
public sealed record PushStoreSnapshot(
    IReadOnlyList<PushSubscription> Subscriptions,
    IReadOnlyDictionary<string, PushSettings> Settings,
    IReadOnlyDictionary<string, PushAlertStateData> AlertStates);

public sealed record PushAlertStateData(DateOnly? LastHandled, IReadOnlyList<string> Low, IReadOnlyList<string> Peak, IReadOnlyList<string>? PeakSoon = null);

public class InMemoryPushStore : IPushStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, PushSubscription> byEndpoint = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PushSettings> settings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StockAlertState> alertStates = new(StringComparer.Ordinal);

    /// <summary>Called, outside the lock, after every change. A persisting store writes its file here.</summary>
    protected virtual void OnChanged()
    {
    }

    public PushStoreSnapshot Snapshot()
    {
        lock (gate)
        {
            return new PushStoreSnapshot(
                [.. byEndpoint.Values],
                new Dictionary<string, PushSettings>(settings),
                alertStates.ToDictionary(a => a.Key, a => new PushAlertStateData(a.Value.LastHandled, [.. a.Value.Memory.Low], [.. a.Value.Memory.Peak], [.. a.Value.Memory.PeakSoon])));
        }
    }

    public void Restore(PushStoreSnapshot snapshot)
    {
        lock (gate)
        {
            byEndpoint.Clear();
            settings.Clear();
            alertStates.Clear();
            foreach (var subscription in snapshot.Subscriptions)
            {
                byEndpoint[subscription.Endpoint] = subscription;
            }

            foreach (var (user, value) in snapshot.Settings)
            {
                settings[user] = value;
            }

            foreach (var (user, value) in snapshot.AlertStates)
            {
                alertStates[user] = new StockAlertState(value.LastHandled, new StockAlertMemory(new HashSet<string>(value.Low, StringComparer.Ordinal), new HashSet<string>(value.Peak, StringComparer.Ordinal)) { PeakSoon = new HashSet<string>(value.PeakSoon ?? [], StringComparer.Ordinal) });
            }
        }
    }

    public Task SaveAsync(PushSubscription subscription, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            byEndpoint[subscription.Endpoint] = subscription;
        }

        OnChanged();
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string userId, string endpoint, CancellationToken cancellationToken = default)
    {
        bool removed;
        lock (gate)
        {
            removed = byEndpoint.TryGetValue(endpoint, out var found) && found.UserId == userId && byEndpoint.Remove(endpoint);
        }

        if (removed)
        {
            OnChanged();
        }

        return Task.FromResult(removed);
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

        OnChanged();
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
            alertStates.Remove(userId);
        }

        OnChanged();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> SubscribedUserIdsAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<string>>([.. byEndpoint.Values.Select(s => s.UserId).Distinct(StringComparer.Ordinal)]);
        }
    }

    public Task<StockAlertState> GetAlertStateAsync(string userId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult(alertStates.GetValueOrDefault(userId) ?? StockAlertState.None);
        }
    }

    public Task SaveAlertStateAsync(string userId, StockAlertState state, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            alertStates[userId] = state;
        }

        OnChanged();
        return Task.CompletedTask;
    }
}
