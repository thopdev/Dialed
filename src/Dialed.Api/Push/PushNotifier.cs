using System.Collections.Concurrent;
using Dialed.Api.Auth;
using Dialed.Api.Friends;
using Dialed.Telemetry;

namespace Dialed.Api.Push;

/// <summary>Told by the services after a change has gone through. It never throws and never makes the caller wait.</summary>
public interface INotifier
{
    /// <summary><paramref name="fromId"/> asked to follow <paramref name="toId"/>.</summary>
    void FollowRequested(string fromId, string toId);

    /// <summary><paramref name="accepterId"/> accepted the request of <paramref name="followerId"/>.</summary>
    void FollowAccepted(string accepterId, string followerId);

    /// <summary><paramref name="joinerId"/> joined the household with an invite code.</summary>
    void HouseholdJoined(string joinerId, string householdId);
}

public sealed class NullNotifier : INotifier
{
    public static readonly NullNotifier Instance = new();

    public void FollowRequested(string fromId, string toId) { }

    public void FollowAccepted(string accepterId, string followerId) { }

    public void HouseholdJoined(string joinerId, string householdId) { }
}

/// <summary>
/// Turns events into pushes (#100): looks up who should hear about it and what they allowed, writes a short line in their language and
/// sends it in the background. A push says a name and what happened, nothing else.
/// </summary>
public sealed class PushNotifier(IUserDirectory users, IFriendStore friends, IPushStore store, IPushSender sender, TimeProvider time, ILogger<PushNotifier> log) : INotifier
{
    private static readonly TimeSpan Remember = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, DateTimeOffset> recent = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> running = new();

    public void FollowRequested(string fromId, string toId) => Run(PushKind.FollowRequest, fromId, [toId]);

    public void FollowAccepted(string accepterId, string followerId) => Run(PushKind.FollowAccepted, accepterId, [followerId]);

    public void HouseholdJoined(string joinerId, string householdId) =>
        Run(PushKind.HouseholdJoined, joinerId, async ct => [.. (await users.ListByHouseholdAsync(householdId, ct)).Select(u => u.Id)]);

    /// <summary>Completes when every push started so far has been tried. For tests and for a clean shutdown.</summary>
    public Task WaitForIdleAsync() => Task.WhenAll(running.Keys);

    private void Run(PushKind kind, string actorId, IReadOnlyList<string> recipients) => Run(kind, actorId, _ => Task.FromResult(recipients));

    private void Run(PushKind kind, string actorId, Func<CancellationToken, Task<IReadOnlyList<string>>> recipients)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                foreach (var recipientId in await recipients(CancellationToken.None))
                {
                    await NotifyAsync(kind, actorId, recipientId);
                }
            }
            catch (Exception e)
            {
                // A push never fails what caused it.
                log.LogWarning(e, "Push notification {Kind} could not be prepared.", kind);
                DialedTelemetry.PushSent.Add(1, new KeyValuePair<string, object?>("type", kind.ToString()), new KeyValuePair<string, object?>("outcome", "failed"));
            }
        });
        running[task] = 0;
        _ = task.ContinueWith(t => running.TryRemove(t, out _), TaskScheduler.Default);
    }

    private async Task NotifyAsync(PushKind kind, string actorId, string recipientId)
    {
        if (recipientId == actorId
            || await users.FindByIdAsync(actorId) is not { } actor
            || await friends.IsBlockedAsync(recipientId, actorId) || await friends.IsBlockedAsync(actorId, recipientId))
        {
            return;
        }

        var settings = await store.GetSettingsAsync(recipientId);
        var wanted = kind == PushKind.HouseholdJoined ? settings.Household : settings.Follows;
        if (!wanted || !Claim(kind, actorId, recipientId))
        {
            Count(kind, "skipped");
            return;
        }

        foreach (var subscription in await store.ListAsync(recipientId))
        {
            var outcome = await sender.SendAsync(subscription, PushText.For(kind, actor.DisplayName, subscription.Language));
            if (outcome == PushOutcome.Gone)
            {
                await store.RemoveAsync(recipientId, subscription.Endpoint);
            }

            Count(kind, outcome.ToString().ToLowerInvariant());
        }
    }

    /// <summary>One push per event: the same actor, kind and person within the hour is the same event.</summary>
    private bool Claim(PushKind kind, string actorId, string recipientId)
    {
        var now = time.GetUtcNow();
        foreach (var old in recent.Where(r => now - r.Value > Remember).Select(r => r.Key).ToList())
        {
            recent.TryRemove(old, out _);
        }

        return recent.TryAdd($"{kind}|{actorId}|{recipientId}", now);
    }

    private static void Count(PushKind kind, string outcome) =>
        DialedTelemetry.PushSent.Add(1, new KeyValuePair<string, object?>("type", kind.ToString()), new KeyValuePair<string, object?>("outcome", outcome));
}

/// <summary>The words of a push, in the language the device told us (English or Dutch).</summary>
public static class PushText
{
    /// <summary>One line for the stock alerts (#259): the bean name for one bag, a count for several. Taps open the inventory filtered to those bags.</summary>
    public static PushPayload ForStock(PushKind kind, IReadOnlyList<string> beanNames, string? language)
    {
        var nl = string.Equals(language, "nl", StringComparison.OrdinalIgnoreCase);
        var many = beanNames.Count > 1;
        if (kind == PushKind.PeakSoon)
        {
            return new(
                nl ? "Bijna op zijn best" : "Almost at its peak",
                many ? (nl ? $"Morgen zijn {beanNames.Count} zakken op hun best." : $"{beanNames.Count} bags reach their peak tomorrow.") : (nl ? $"{beanNames[0]} is morgen op zijn best." : $"{beanNames[0]} reaches its peak tomorrow."),
                "inventory",
                "peak-soon");
        }

        return kind == PushKind.LowStock
            ? new(
                nl ? "Bijna op" : "Running low",
                many ? (nl ? $"{beanNames.Count} zakken raken op." : $"{beanNames.Count} bags are running low.") : (nl ? $"{beanNames[0]} raakt op." : $"{beanNames[0]} is running low."),
                "inventory?show=low",
                "low-stock")
            : new(
                nl ? "Op zijn best" : "At its peak",
                many ? (nl ? $"{beanNames.Count} zakken zijn op hun best." : $"{beanNames.Count} bags are at their peak.") : (nl ? $"{beanNames[0]} is op zijn best." : $"{beanNames[0]} is at its peak."),
                "inventory?show=peak",
                "peak");
    }

    public static PushPayload For(PushKind kind, string name, string? language)
    {
        var nl = string.Equals(language, "nl", StringComparison.OrdinalIgnoreCase);
        return kind switch
        {
            PushKind.FollowRequest => new(
                nl ? "Nieuw volgverzoek" : "New follow request",
                nl ? $"{name} wil je volgen." : $"{name} wants to follow you.",
                "people",
                "follow-request"),
            PushKind.FollowAccepted => new(
                nl ? "Verzoek geaccepteerd" : "Request accepted",
                nl ? $"{name} heeft je volgverzoek geaccepteerd." : $"{name} accepted your follow request.",
                "people",
                "follow-accepted"),
            _ => new(
                nl ? "Nieuw huishoudlid" : "New household member",
                nl ? $"{name} is lid geworden van je huishouden." : $"{name} joined your household.",
                "settings#settings-household",
                "household-joined"),
        };
    }
}
