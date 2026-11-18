using Dialed.Api.Data;
using Microsoft.Azure.Cosmos;

namespace Dialed.Api.Tests;

/// <summary>
/// A throwaway database on the Cosmos DB in <c>COSMOS_CONNECTION_STRING</c> (the emulator in CI's <c>cosmos</c> job, or locally) for one test
/// class instance. Every <see cref="NewCosmos"/> gets a new client, as a restarted or second API instance would. Skips the test when no
/// connection string is set. The database is deleted afterwards.
/// </summary>
public sealed class CosmosTestDatabase : IAsyncDisposable
{
    public static readonly string? ConnectionString = Environment.GetEnvironmentVariable("COSMOS_CONNECTION_STRING");

    private readonly string database = $"dialed-tests-{Guid.NewGuid():N}";
    private readonly List<CosmosClient> clients = [];

    public DialedCosmos NewCosmos()
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

/// <summary>A clock the tests move by hand.</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
