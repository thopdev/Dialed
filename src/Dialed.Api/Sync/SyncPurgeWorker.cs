namespace Dialed.Api.Sync;

/// <summary>Once a day removes records that were deleted more than 30 days ago, so the database stays tidy.</summary>
public sealed class SyncPurgeWorker(ISyncStore store, TimeProvider time, ILogger<SyncPurgeWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var counts = await store.PurgeDeletedAsync(time.GetUtcNow() - Retention, cancellationToken);
        foreach (var (table, count) in counts)
        {
            logger.LogInformation("Purged {Count} deleted records from {Table}", count, table);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24), time);
        try
        {
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
