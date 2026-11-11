using Dialed.Client.Uploads;

namespace Dialed.UI.Tests;

/// <summary>An upload queue that never uploads: tests decide each photo's state.</summary>
public sealed class TestUploadQueue : IUploadQueue
{
    private readonly Dictionary<string, UploadStatus> states = [];

    public List<string> Retried { get; } = [];

    public IReadOnlyList<UploadItem> Items => [.. states.Select(s => new UploadItem(s.Key, s.Value, 0, null, null))];

    public int PendingCount => states.Values.Count(s => s != UploadStatus.Failed);

    public int FailedCount => states.Values.Count(s => s == UploadStatus.Failed);

    public event Action? Changed;

    public void Set(string photoId, UploadStatus? status)
    {
        if (status is { } value)
        {
            states[photoId] = value;
        }
        else
        {
            states.Remove(photoId);
        }

        Changed?.Invoke();
    }

    public UploadStatus? StatusOf(string photoId) => states.TryGetValue(photoId, out var status) ? status : null;

    public Task EnqueueAsync(string photoId, CancellationToken cancellationToken = default)
    {
        Set(photoId, UploadStatus.Waiting);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string photoId, CancellationToken cancellationToken = default)
    {
        Set(photoId, null);
        return Task.CompletedTask;
    }

    public Task RetryAsync(string? photoId = null, CancellationToken cancellationToken = default)
    {
        foreach (var id in states.Where(s => s.Value == UploadStatus.Failed && (photoId is null || s.Key == photoId)).Select(s => s.Key).ToList())
        {
            Retried.Add(id);
            states[id] = UploadStatus.Waiting;
        }

        Changed?.Invoke();
        return Task.CompletedTask;
    }
}
