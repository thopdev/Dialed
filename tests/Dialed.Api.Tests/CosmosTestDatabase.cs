using Dialed.Api.Data;
using Microsoft.Azure.Cosmos;

namespace Dialed.Api.Tests;

/// <summary>
/// A throwaway Cosmos DB database for one test class instance (the emulator in CI's <c>cosmos</c> job, or locally; see
/// <c>docs/cosmos-stores.md</c>). <see cref="New"/> skips the test without <c>COSMOS_CONNECTION_STRING</c> and returns a
/// <see cref="DialedCosmos"/> over a new client, as a restarted or second API instance would have. The database is deleted on dispose.
/// </summary>
public sealed class CosmosTestDatabase : IAsyncDisposable
{
    public static readonly string? ConnectionString = Environment.GetEnvironmentVariable("COSMOS_CONNECTION_STRING");

    private readonly string database = $"dialed-tests-{Guid.NewGuid():N}";
    private readonly List<CosmosClient> clients = [];

    public DialedCosmos New()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(ConnectionString), "COSMOS_CONNECTION_STRING not set (runs in CI's cosmos job).");
        var client = CosmosConnection.CreateClient(ConnectionString!);
        clients.Add(client);
        return new DialedCosmos(client, database);
    }

    public async ValueTask DisposeAsync()
    {
        if (clients.Count > 0)
        {
            try
            {
                await clients[0].GetDatabase(database).DeleteAsync();
            }
            catch (CosmosException)
            {
                // never created
            }
        }

        foreach (var client in clients)
        {
            client.Dispose();
        }
    }
}
