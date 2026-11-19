using System.Net;
using Microsoft.Azure.Cosmos;

namespace Dialed.Api.Data;

/// <summary>
/// In Azure (managed identity, containers from Bicep) checks once at startup that the API can reach its database: it lists the
/// containers and does one point read per container. A 404 for the probe document is the expected answer; 401/403 means the
/// data-plane role assignment is missing. Only logs: startup and <c>/health</c> never wait for Cosmos DB.
/// </summary>
public sealed partial class CosmosStartupCheck(DialedCosmos cosmos, ILogger<CosmosStartupCheck> log) : BackgroundService
{
    private const string ProbeId = "__startup-check";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var ids = new List<string>();
            using (var iterator = cosmos.Client.GetDatabase(cosmos.DatabaseId).GetContainerQueryIterator<ContainerProperties>())
            {
                while (iterator.HasMoreResults)
                {
                    foreach (var container in await iterator.ReadNextAsync(stoppingToken))
                    {
                        ids.Add(container.Id);
                        await ProbeAsync(container, stoppingToken);
                    }
                }
            }

            Reachable(log, cosmos.Client.Endpoint, cosmos.DatabaseId, string.Join(", ", ids.Order(StringComparer.Ordinal)));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Unreachable(log, exception, cosmos.Client.Endpoint, cosmos.DatabaseId);
        }
    }

    private async Task ProbeAsync(ContainerProperties properties, CancellationToken cancellationToken)
    {
        // The probe id doubles as the partition key value: an empty logical partition whatever the key path is.
        using var response = await cosmos.Client.GetContainer(cosmos.DatabaseId, properties.Id)
            .ReadItemStreamAsync(ProbeId, new PartitionKey(ProbeId), cancellationToken: cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cosmos DB reachable at {Endpoint}, database {Database}, containers: {Containers}")]
    private static partial void Reachable(ILogger logger, Uri endpoint, string database, string containers);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cosmos DB not reachable at {Endpoint}, database {Database}: check the data-plane role assignment and the containers in Bicep")]
    private static partial void Unreachable(ILogger logger, Exception exception, Uri endpoint, string database);
}
