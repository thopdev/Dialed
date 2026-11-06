using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Datasync.Server;
using CommunityToolkit.Datasync.Server.Abstractions.Json;
using CommunityToolkit.Datasync.Server.CosmosDb;
using DatasyncSpike;
using DatasyncSpike.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Azure.Cosmos;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("cosmos")
    ?? throw new InvalidOperationException("ConnectionStrings:cosmos is not set.");

var cosmosClient = SpikeCosmos.CreateClient(connectionString);
builder.Services.AddSingleton(cosmosClient);

builder.Services.AddSingleton<ICosmosTableOptions<Brew>>(new HouseholdTableOptions<Brew>(SpikeCosmos.DatabaseId, SpikeCosmos.ContainerId));
builder.Services.AddSingleton(typeof(IRepository<>), typeof(CosmosTableRepository<>));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAccessControlProvider<Brew>, HouseholdAccessControl<Brew>>();

builder.Services
    .AddAuthentication(SpikeAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SpikeAuthenticationHandler>(SpikeAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization();

builder.Services.AddDatasyncServices();
builder.Services.AddControllers();

var app = builder.Build();

await SpikeCosmos.EnsureContainerAsync(cosmosClient);

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

public static class SpikeCosmos
{
    public const string DatabaseId = "dialed-spike";
    public const string ContainerId = "household-data";

    public static CosmosClient CreateClient(string connectionString) => new(connectionString, new CosmosClientOptions
    {
        // The Linux (vNext) emulator only supports gateway mode.
        ConnectionMode = connectionString.Contains("localhost", StringComparison.OrdinalIgnoreCase) || connectionString.Contains("127.0.0.1", StringComparison.Ordinal)
            ? ConnectionMode.Gateway
            : ConnectionMode.Direct,
        LimitToEndpoint = true,
        UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions
        {
            Converters =
            {
                new JsonStringEnumConverter(),
                new DateTimeOffsetConverter(),
                new DateTimeConverter(),
                new TimeOnlyConverter(),
            },
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        },
    });

    /// <summary>One container for all household data, partitioned by householdId; composite index for delta pulls.</summary>
    public static async Task EnsureContainerAsync(CosmosClient client)
    {
        Database database = await client.CreateDatabaseIfNotExistsAsync(DatabaseId);
        await database.CreateContainerIfNotExistsAsync(new ContainerProperties(ContainerId, "/householdId")
        {
            IndexingPolicy = new IndexingPolicy
            {
                CompositeIndexes =
                {
                    new Collection<CompositePath>
                    {
                        new() { Path = "/updatedAt", Order = CompositePathSortOrder.Ascending },
                        new() { Path = "/id", Order = CompositePathSortOrder.Ascending },
                    },
                },
            },
        });
    }
}
