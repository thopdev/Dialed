using System.Text.Json;
using Dialed.Api.Auth;
using Dialed.Api.Sync;
using Dialed.Domain.Equipment;
using Dialed.Domain.Inventory;
using Dialed.Domain.Model;
using Dialed.Telemetry;

namespace Dialed.Api.Push;

/// <summary>
/// The daily stock alert job (#259). For everyone with a device whose local time has reached about 08:00 and who has not been looked at
/// today, it judges their household's bags with the same Domain code the app uses and sends at most one low-stock and one peak
/// notification, combined. It never runs at night in the person's own time zone.
/// </summary>
public sealed class StockAlertService(IPushStore store, ISyncStore sync, IUserDirectory users, IPushSender sender, ILogger<StockAlertService> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Looks at everyone who is due at <paramref name="utcNow"/>. Safe to call as often as wanted: a person is handled once per local day.</summary>
    public async Task RunAsync(DateTimeOffset utcNow, CancellationToken ct = default)
    {
        foreach (var userId in await store.SubscribedUserIdsAsync(ct))
        {
            try
            {
                await HandleAsync(userId, utcNow, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // One person's problem never stops the others.
                log.LogWarning(e, "Stock alerts could not be handled for a person.");
            }
        }
    }

    private async Task HandleAsync(string userId, DateTimeOffset utcNow, CancellationToken ct)
    {
        var settings = await store.GetSettingsAsync(userId, ct);
        if (!settings.LowStock && !settings.Peak && !settings.Service)
        {
            return;
        }

        var zone = Zone(settings.TimeZone);
        var state = await store.GetAlertStateAsync(userId, ct);
        if (!StockAlerts.IsDue(utcNow, zone, state.LastHandled) || await users.FindByIdAsync(userId, ct) is not { } user)
        {
            return;
        }

        var inventory = settings.Inventory ?? InventorySettings.Default;
        var today = StockAlerts.LocalDate(utcNow, zone);
        var memory = state.Memory;
        List<string> low = [];
        List<string> peak = [];
        List<string> peakSoon = [];
        List<string> gearDue = [];
        var gearMemory = memory.Gear;
        foreach (var household in user.AllHouseholdIds)
        {
            var stock = InventoryOverview.Build(
                await ReadAsync<Bag>(household, userId, "bags", ct),
                await ReadAsync<Bean>(household, userId, "beans", ct),
                await ReadAsync<Brew>(household, userId, "brews", ct),
                today,
                inventory);
            var result = StockAlerts.Evaluate(household, stock, memory, settings.LowStock, settings.Peak);
            low.AddRange(result.LowStock);
            peak.AddRange(result.Peak);
            peakSoon.AddRange(result.PeakSoon);
            memory = result.Memory;

            if (settings.Service)
            {
                var gear = await ReadAsync<EquipmentItem>(household, userId, "equipment", ct);
                var upkeep = GearAlerts.Evaluate(
                    household,
                    gear,
                    await ReadAsync<Brew>(household, userId, "brews", ct),
                    await ReadAsync<Recipe>(household, userId, "recipes", ct),
                    today,
                    gearMemory);
                gearDue.AddRange(upkeep.Due);
                gearMemory = upkeep.Memory;
            }
        }

        // Switching the alert off forgets what was told, so it speaks again when switched back on.
        memory = new StockAlertMemory(memory.Low, memory.Peak) { PeakSoon = memory.PeakSoon, Gear = settings.Service ? gearMemory : new HashSet<string>() };

        // Remembered before sending: a crash in between loses one notification rather than sending it twice.
        await store.SaveAlertStateAsync(userId, new StockAlertState(today, memory), ct);

        var devices = await store.ListAsync(userId, ct);
        if (low.Count > 0)
        {
            await SendAsync(userId, devices, PushKind.LowStock, low, ct);
        }

        if (peakSoon.Count > 0)
        {
            await SendAsync(userId, devices, PushKind.PeakSoon, peakSoon, ct);
        }

        if (peak.Count > 0)
        {
            await SendAsync(userId, devices, PushKind.Peak, peak, ct);
        }

        if (gearDue.Count > 0)
        {
            await SendAsync(userId, devices, PushKind.ServiceDue, gearDue, ct);
        }
    }

    private async Task SendAsync(string userId, IReadOnlyList<PushSubscription> devices, PushKind kind, IReadOnlyList<string> names, CancellationToken ct)
    {
        foreach (var device in devices)
        {
            var outcome = await sender.SendAsync(device, PushText.ForStock(kind, names, device.Language), ct);
            if (outcome == PushOutcome.Gone)
            {
                await store.RemoveAsync(userId, device.Endpoint, ct);
            }

            DialedTelemetry.PushSent.Add(1, new KeyValuePair<string, object?>("type", kind.ToString()), new KeyValuePair<string, object?>("outcome", outcome.ToString().ToLowerInvariant()));
        }
    }

    /// <summary>Everything the person may read of one table of a household, as the Domain model.</summary>
    private async Task<IReadOnlyList<T>> ReadAsync<T>(string household, string userId, string table, CancellationToken ct)
    {
        List<T> list = [];
        DateTimeOffset? after = null;
        string? afterId = null;
        while (true)
        {
            var (rows, hasMore) = await sync.PullAsync(household, userId, table, after, afterId, 500, ct);
            foreach (var row in rows)
            {
                try
                {
                    if (row.Entity.Deserialize<T>(Json) is { } item)
                    {
                        list.Add(item);
                    }
                }
                catch (JsonException)
                {
                    // A row this version cannot read is left out of the judgement.
                }
            }

            if (!hasMore || rows.Count == 0)
            {
                return list;
            }

            (after, afterId) = (rows[^1].UpdatedAt, rows[^1].Id);
        }
    }

    private static TimeZoneInfo Zone(string? id) =>
        id is not null && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;
}

/// <summary>Wakes the stock alert job every hour. A hosted service now; in Azure a timer-triggered Function takes over (#260).</summary>
public sealed class StockAlertWorker(StockAlertService service, TimeProvider time, ILogger<StockAlertWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        do
        {
            try
            {
                await service.RunAsync(time.GetUtcNow(), stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "The stock alert job failed; it tries again next hour.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
