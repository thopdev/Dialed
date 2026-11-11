using System.Threading.Channels;
using Dialed.Images;

namespace Dialed.Api.Photos;

/// <summary>A blob-created event that failed every attempt, or could never succeed.</summary>
public sealed record PoisonedPhotoEvent(string BlobPath, int Attempts, string Error, DateTimeOffset At);

/// <summary>
/// Stands in for the blob-triggered Azure Function (#69) while there is no Blob Storage: listens to <see cref="IPhotoBlobEvents.BlobWritten"/>,
/// runs <see cref="PhotoProcessingHandler"/> one event at a time, retries with backoff and parks events that keep failing, like the
/// Functions runtime does with its <c>webjobs-blobtrigger-poison</c> queue.
/// </summary>
public sealed partial class InMemoryPhotoTrigger : BackgroundService
{
    /// <summary>Same as the Functions blob trigger's default <c>maxDequeueCount</c>.</summary>
    public const int MaxAttempts = 5;

    private readonly Channel<string> events = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock gate = new();
    private readonly List<PoisonedPhotoEvent> poisoned = [];
    private readonly IServiceScopeFactory scopes;
    private readonly TimeProvider time;
    private readonly ILogger<InMemoryPhotoTrigger> logger;
    private int pending;

    public InMemoryPhotoTrigger(IPhotoBlobEvents store, IServiceScopeFactory scopes, TimeProvider time, ILogger<InMemoryPhotoTrigger> logger)
    {
        this.scopes = scopes;
        this.time = time;
        this.logger = logger;
        store.BlobWritten += path =>
        {
            if (path.StartsWith(PhotoBlobPaths.OriginalsFolder + "/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref pending);
                events.Writer.TryWrite(path);
            }
        };
    }

    /// <summary>First retry delay; doubles each attempt. Tests set it to zero.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    public IReadOnlyList<PoisonedPhotoEvent> Poisoned
    {
        get
        {
            lock (gate)
            {
                return [.. poisoned];
            }
        }
    }

    /// <summary>Events written but not yet finished (processed or poisoned).</summary>
    public int Pending => Volatile.Read(ref pending);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var path in events.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunAsync(path, stoppingToken);
            }
            finally
            {
                Interlocked.Decrement(ref pending);
            }
        }
    }

    /// <summary>One event with retries; the outcome, or <c>null</c> when it was poisoned.</summary>
    public async Task<PhotoProcessingOutcome?> RunAsync(string blobPath, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var outcome = await scope.ServiceProvider.GetRequiredService<PhotoProcessingHandler>().HandleAsync(blobPath, cancellationToken);
                LogProcessed(blobPath, outcome);
                return outcome;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (error is InvalidPhotoException || attempt >= MaxAttempts)
                {
                    Poison(blobPath, attempt, error);
                    return null;
                }

                LogRetry(error, blobPath, attempt);
                await Task.Delay(RetryDelay * Math.Pow(2, attempt - 1), time, cancellationToken);
            }
        }
    }

    private void Poison(string blobPath, int attempts, Exception error)
    {
        lock (gate)
        {
            poisoned.Add(new PoisonedPhotoEvent(blobPath, attempts, error.Message, time.GetUtcNow()));
        }

        LogPoisoned(error, blobPath, attempts);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Photo {BlobPath}: {Outcome}")]
    private partial void LogProcessed(string blobPath, PhotoProcessingOutcome outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "Photo {BlobPath} failed attempt {Attempt}; retrying")]
    private partial void LogRetry(Exception error, string blobPath, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Photo {BlobPath} moved to the poison list after {Attempts} attempt(s)")]
    private partial void LogPoisoned(Exception error, string blobPath, int attempts);
}
