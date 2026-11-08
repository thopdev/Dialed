using Dialed.Domain.Model;

namespace Dialed.Client.Sync;

/// <summary>
/// Talks to the server. The real implementation speaks the Datasync REST protocol (filtered, paged queries on <c>updatedAt</c>,
/// <c>__includedeleted</c>, <c>If-Match</c> versions) and arrives with the API (#38); tests use a fake server.
/// </summary>
public interface ISyncTransport
{
    /// <summary>
    /// Sends the record (soft-deleted records too). <paramref name="ifMatchVersion"/> is the server version the edit started from,
    /// or <c>null</c> for a record the server has never seen. Throws <see cref="SyncTransportException"/> when the server cannot be reached.
    /// </summary>
    Task<PushResult<T>> PushAsync<T>(string table, T entity, string? ifMatchVersion, CancellationToken cancellationToken = default)
        where T : SyncEntity;

    /// <summary>Records changed after <paramref name="after"/>, oldest first, deleted ones included.</summary>
    Task<PullPage<T>> PullAsync<T>(string table, SyncWatermark? after, int pageSize, CancellationToken cancellationToken = default)
        where T : SyncEntity;
}
