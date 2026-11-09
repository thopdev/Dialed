namespace Dialed.Client.Storage;

/// <summary>Keeps documents in memory only: the default until a host registers a durable store, and the store used in tests.</summary>
public sealed class InMemoryLocalStore : ILocalStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Dictionary<string, string>> collections = [];

    public Task<IReadOnlyDictionary<string, string>> ReadAllAsync(string collection, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyDictionary<string, string>>(
                collections.TryGetValue(collection, out var documents) ? new Dictionary<string, string>(documents) : []);
        }
    }

    public Task PutAsync(string collection, string key, string json, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (!collections.TryGetValue(collection, out var documents))
            {
                collections[collection] = documents = [];
            }

            documents[key] = json;
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string collection, string key, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (collections.TryGetValue(collection, out var documents))
            {
                documents.Remove(key);
            }
        }

        return Task.CompletedTask;
    }

    public Task ClearCollectionAsync(string collection, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            collections.Remove(collection);
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            collections.Clear();
        }

        return Task.CompletedTask;
    }
}
