using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dialed.Api.Data;

/// <summary>A container a store needs: its id, partition key path and default TTL (-1: TTL on, set per document; null: off).</summary>
public sealed record CosmosContainerSpec(string Id, string PartitionKeyPath, int? DefaultTimeToLive = -1);

/// <summary>
/// The API's Cosmos DB database: one client, one database per environment, containers created on first use.
/// Stores ask for their container with a <see cref="CosmosContainerSpec"/>; see <c>docs/cosmos-stores.md</c>.
/// </summary>
public sealed class DialedCosmos(CosmosClient client, string databaseId)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<Container>>> containers = new(StringComparer.Ordinal);

    public CosmosClient Client => client;

    public string DatabaseId => databaseId;

    /// <summary>The container, created (with its database) the first time any instance asks. A failed attempt is retried on the next call.</summary>
    public async Task<Container> ContainerAsync(CosmosContainerSpec spec, CancellationToken cancellationToken)
    {
        var lazy = containers.GetOrAdd(spec.Id, _ => new Lazy<Task<Container>>(() => CreateAsync(spec)));
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken);
        }
        catch (Exception) when (lazy.Value.IsFaulted)
        {
            containers.TryRemove(new KeyValuePair<string, Lazy<Task<Container>>>(spec.Id, lazy));
            throw;
        }
    }

    private async Task<Container> CreateAsync(CosmosContainerSpec spec)
    {
        Database database = await client.CreateDatabaseIfNotExistsAsync(databaseId);
        Container container = await database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(spec.Id, spec.PartitionKeyPath) { DefaultTimeToLive = spec.DefaultTimeToLive });
        return container;
    }
}

/// <summary>Reads the Cosmos DB connection and creates the client the same way for every store.</summary>
public static class CosmosConnection
{
    public const string DefaultDatabase = "dialed";

    /// <summary>
    /// The connection string and database name, or <c>null</c> when Cosmos DB is not configured (the in-memory stores are used then).
    /// The AppHost's database reference sets <c>ConnectionStrings:dialed</c> (with <c>Database=dialed</c>); <c>ConnectionStrings:cosmos</c>
    /// is the account-level alternative.
    /// </summary>
    public static (string ConnectionString, string Database)? From(IConfiguration configuration)
    {
        var raw = configuration.GetConnectionString("dialed") ?? configuration.GetConnectionString("cosmos");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Aspire adds "Database=dialed" to a database reference; the Cosmos DB client does not know that key.
        var database = DefaultDatabase;
        var parts = new List<string>();
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("Database=", StringComparison.OrdinalIgnoreCase))
            {
                database = part["Database=".Length..] is { Length: > 0 } name ? name : DefaultDatabase;
            }
            else
            {
                parts.Add(part);
            }
        }

        return (string.Join(';', parts) + ";", database);
    }

    public static CosmosClient CreateClient(string connectionString) => new(connectionString, new CosmosClientOptions
    {
        // The Linux (vNext) emulator only supports gateway mode.
        ConnectionMode = IsLocal(connectionString) ? ConnectionMode.Gateway : ConnectionMode.Direct,
        LimitToEndpoint = true,
        UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        },
    });

    private static bool IsLocal(string connectionString) =>
        connectionString.Contains("localhost", StringComparison.OrdinalIgnoreCase) || connectionString.Contains("127.0.0.1", StringComparison.Ordinal);
}

public static class CosmosServiceCollectionExtensions
{
    /// <summary>Registers the shared client and <see cref="DialedCosmos"/> once. False (and nothing registered) when Cosmos DB is not configured.</summary>
    public static bool TryAddDialedCosmos(this IServiceCollection services, IConfiguration configuration)
    {
        if (CosmosConnection.From(configuration) is not { } cosmos)
        {
            return false;
        }

        services.TryAddSingleton(_ => CosmosConnection.CreateClient(cosmos.ConnectionString));
        services.TryAddSingleton(sp => new DialedCosmos(sp.GetRequiredService<CosmosClient>(), cosmos.Database));
        return true;
    }

    /// <summary>The Cosmos DB store when Cosmos DB is configured, otherwise the in-memory one (tests, a bare <c>dotnet run</c>).</summary>
    public static IServiceCollection AddCosmosOrInMemory<TService, TCosmos, TInMemory>(this IServiceCollection services, IConfiguration configuration)
        where TService : class
        where TCosmos : class, TService
        where TInMemory : class, TService
    {
        if (services.TryAddDialedCosmos(configuration))
        {
            services.AddSingleton<TService, TCosmos>();
        }
        else
        {
            services.AddSingleton<TService, TInMemory>();
        }

        return services;
    }
}
