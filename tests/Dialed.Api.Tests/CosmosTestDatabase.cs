using Dialed.Api.Data;
using Microsoft.Azure.Cosmos;

namespace Dialed.Api.Tests;

/// <summary>
/// A database of its own on the Cosmos DB emulator, removed afterwards. Skips the test without <c>COSMOS_CONNECTION_STRING</c>
/// (it runs in CI's <c>cosmos</c> job; see <c>docs/cosmos-stores.md</c>). Each <see cref="New"/> is a new client, as a restarted
/// or second API instance would have.
/// </summary>
public sealed class CosmosTestDatabase : IAsyncDisposable
{
    public static readonly string? ConnectionString = Environment.GetEnvironmentVariable("COSMOS_CONNECTION_STRING");

    private readonly List<CosmosClient> clients = [];

    public string Name { get; } = $"dialed-tests-{Guid.NewGuid():N}";

    public static void SkipWithoutEmulator() =>
        Assert.SkipWhen(string.IsNullOrEmpty(ConnectionString), "COSMOS_CONNECTION_STRING not set (runs in CI's cosmos job).");

    public DialedCosmos New()
    {
        SkipWithoutEmulator();
        var client = CosmosConnection.CreateClient(ConnectionString!);
        clients.Add(client);
        return new DialedCosmos(client, Name);
    }

    public async ValueTask DisposeAsync()
    {
        if (clients.Count > 0)
        {
            try
            {
                await clients[0].GetDatabase(Name).DeleteAsync();
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
