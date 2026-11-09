using Dialed.Client.Storage;
using Microsoft.EntityFrameworkCore;

namespace Dialed.Web.Storage;

/// <summary>
/// The durable <see cref="ILocalStore"/> for the browser: SQLite compiled to WebAssembly (Bit.Bsqlite), kept in the origin
/// private file system with IndexedDB as fallback. One operation at a time, so contexts never overlap on the single connection.
/// </summary>
public sealed class BsqliteLocalStore(IDbContextFactory<LocalDbContext> contexts) : ILocalStore
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public Task<IReadOnlyDictionary<string, string>> ReadAllAsync(string collection, CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyDictionary<string, string>>(async db =>
            await db.Documents.AsNoTracking().Where(d => d.Collection == collection).ToDictionaryAsync(d => d.Key, d => d.Json, cancellationToken), cancellationToken);

    public Task PutAsync(string collection, string key, string json, CancellationToken cancellationToken = default) =>
        RunAsync(async db =>
        {
            var row = await db.Documents.FindAsync([collection, key], cancellationToken);
            if (row is null)
            {
                db.Documents.Add(new DocumentRow { Collection = collection, Key = key, Json = json });
            }
            else
            {
                row.Json = json;
            }

            await db.SaveChangesAsync(cancellationToken);
            return 0;
        }, cancellationToken);

    public Task RemoveAsync(string collection, string key, CancellationToken cancellationToken = default) =>
        RunAsync(async db =>
        {
            var row = await db.Documents.FindAsync([collection, key], cancellationToken);
            if (row is not null)
            {
                db.Documents.Remove(row);
                await db.SaveChangesAsync(cancellationToken);
            }

            return 0;
        }, cancellationToken);

    public Task ClearCollectionAsync(string collection, CancellationToken cancellationToken = default) =>
        RunAsync(async db =>
        {
            db.Documents.RemoveRange(await db.Documents.Where(d => d.Collection == collection).ToListAsync(cancellationToken));
            await db.SaveChangesAsync(cancellationToken);
            return 0;
        }, cancellationToken);

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async db =>
        {
            db.Documents.RemoveRange(await db.Documents.ToListAsync(cancellationToken));
            await db.SaveChangesAsync(cancellationToken);
            return 0;
        }, cancellationToken);

    private async Task<T> RunAsync<T>(Func<LocalDbContext, Task<T>> work, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
            return await work(db);
        }
        finally
        {
            gate.Release();
        }
    }
}
