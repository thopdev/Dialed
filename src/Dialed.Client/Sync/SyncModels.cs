using Dialed.Domain.Model;

namespace Dialed.Client.Sync;

/// <summary>A local change waiting to be pushed. The record itself is read when pushing, so several saves of one record are one entry.</summary>
public sealed record OutboxEntry(long Sequence, string Table, string EntityId, int Attempts = 0, string? LastError = null);

/// <summary>Where the last pull stopped: records are ordered by update time, then id, so records with the same time are not skipped.</summary>
public sealed record SyncWatermark(DateTimeOffset UpdatedAt, string Id);

/// <summary>A record as the server holds it, with the server's version token (used as <c>If-Match</c> when pushing).</summary>
public sealed record ServerRecord<T>(T Entity, string Version)
    where T : SyncEntity;

public enum PushOutcome
{
    /// <summary>The server stored the record; <see cref="PushResult{T}.Record"/> is what it stored.</summary>
    Accepted,

    /// <summary>The server's copy changed since the version we sent; <see cref="PushResult{T}.Record"/> is the server's copy.</summary>
    Conflict,

    /// <summary>The server refused the record for good (for example no access); retrying will not help.</summary>
    Rejected,
}

public sealed record PushResult<T>(PushOutcome Outcome, ServerRecord<T>? Record = null, string? Message = null)
    where T : SyncEntity;

/// <summary>Records changed after a watermark, oldest first, including deleted ones.</summary>
public sealed record PullPage<T>(IReadOnlyList<ServerRecord<T>> Records, bool HasMore)
    where T : SyncEntity;

/// <summary>The network is down or the server failed: the sync stops and tries again later. Not for refusals (see <see cref="PushOutcome.Rejected"/>).</summary>
public sealed class SyncTransportException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A change that lost against the server and was overwritten by this device (last writer wins); kept so it can be shown later.</summary>
public sealed record ConflictLogEntry(string Table, string EntityId, DateTimeOffset At, string OverwrittenServerCopy);

public enum SyncOutcome
{
    Completed,

    /// <summary>The server could not be reached; local changes stay queued.</summary>
    Offline,

    /// <summary>Another sync was already running.</summary>
    AlreadyRunning,
}

/// <summary>How far a pull has come. <paramref name="FirstPull"/>: nothing had been pulled before (new device, or the local database was cleared).</summary>
public sealed record PullProgress(bool FirstPull, int Applied);

public sealed record SyncResult(SyncOutcome Outcome, int Pushed = 0, int Pulled = 0, int Conflicts = 0, int Failed = 0)
{
    public static SyncResult AlreadyRunning { get; } = new(SyncOutcome.AlreadyRunning);
}
