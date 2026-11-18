# Cosmos DB stores in the API

How a store in `Dialed.Api` moves from memory to Cosmos DB (ADR-0003). The first one is the email recovery store (#197); the sync store (#38) and the identity stores follow the same pattern.

## The pieces (`src/Dialed.Api/Data/DialedCosmos.cs`)

| Piece | What it does |
| --- | --- |
| `CosmosConnection.Resolve(configuration)` | Picks the connection, in this order: a connection string (`From`, below) for the AppHost and the emulator; else `Cosmos:Endpoint` with `Cosmos:DatabaseName` (default `dialed`), which Azure sets; else `null` (in-memory stores). |
| `CosmosConnection.From(configuration)` | Reads `ConnectionStrings:dialed` (the AppHost's database reference, which carries `Database=dialed`) or else `ConnectionStrings:cosmos`. Returns `null` when neither is set. |
| `CosmosConnection.CreateClient` | One client for the whole API: the key from the connection string, or `DefaultAzureCredential` for an endpoint (the API's user-assigned managed identity through `AZURE_CLIENT_ID`). Gateway mode for the local vNext emulator, System.Text.Json with camelCase names. |
| `DialedCosmos` | Singleton with the client and the database. With a connection string, `ContainerAsync(spec)` creates the database and container on first use (`CreateIfNotExists`), so startup never waits for Cosmos DB. With an endpoint (`CreatesContainers` is false) it only hands out the container: data-plane RBAC can't create containers, so they come from Bicep. |
| `CosmosContainerSpec(id, partitionKeyPath, defaultTtl)` | What a store needs. The default TTL of `-1` turns TTL on, and each document then sets its own `ttl`. |
| `services.AddCosmosOrInMemory<TService, TCosmos, TInMemory>(configuration)` | Registers the Cosmos DB store when Cosmos DB is configured, otherwise the in-memory one. |

## Adding a store

1. Keep the interface and the in-memory implementation. Tests, `WebApplicationFactory` apps and a bare `dotnet run` keep using it.
2. Write `CosmosXStore(DialedCosmos cosmos, ...)` with a static `CosmosContainerSpec`. Choose the partition key from how the store is read: everything one request needs should sit in one logical partition, so reads are point reads (1 RU). For example, recovery uses `/accountId` and household data uses `/householdId`.
3. Register it with `AddCosmosOrInMemory<...>(configuration)` in the feature's `AddDialed...` method.
4. Add the container to Azure (below). Without it the store gets `404 NotFound` in Azure, while it works locally.
5. Concurrency: read the document with its ETag and write with `IfMatchEtag`. On `412`/`409`, read again and retry a few times. Never read, change and write without the ETag.
6. Expiry: give documents that stop mattering a `ttl` in seconds (their window plus a margin). TTL runs on the server clock, so reads still compare against the injected `TimeProvider`.
7. Tests: write one abstract contract class with both a `...InMemory...Tests` and a `...Cosmos...Tests` subclass. Add Cosmos-only tests for what memory can't show, such as a new instance seeing the state or two instances racing. Cosmos tests call `Assert.SkipWhen(string.IsNullOrEmpty(COSMOS_CONNECTION_STRING), ...)` and use their own database, deleted afterwards.

## Running the Cosmos tests

CI's `cosmos` job starts the vNext emulator and runs `--filter-class "*Cosmos*"` on `Dialed.Api.Tests`. Locally:

```bash
docker run -d -p 8081:8081 mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-latest
```

```bash
COSMOS_CONNECTION_STRING="AccountEndpoint=http://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;" dotnet test --project tests/Dialed.Api.Tests --filter-class "*Cosmos*"
```

The key is the emulator's public, well-known key.

## Containers in Azure

In Azure the API connects with its managed identity and the built-in **Cosmos DB Built-in Data Contributor** role, scoped to the environment's database (`infra/modules/cosmos-database.bicep`). That role reads and writes documents but can't create databases or containers, so every container is declared in Bicep, in the `containers` list of that module:

```bicep
var containers = [
  // What the container holds (issue, store class).
  { id: 'identity', partitionKey: '/pk', defaultTtl: -1 }
  { id: 'recovery', partitionKey: '/accountId', defaultTtl: -1 }
]
```

To add one, add a comment line and an entry line at its alphabetical place by `id`, matching the store's `CosmosContainerSpec` (`defaultTtl: -1` turns TTL on with a per-document `ttl`; leave `defaultTtl` out for no TTL). Different PRs then touch different lines; on a merge conflict keep both entries. The next `azd provision` (the deploy workflow runs it when `infra/` changed) creates the container. Never change the partition key of an existing container: Cosmos DB can't, the deployment fails, and the data has to be moved to a new container.

All containers share the database's 400 RU/s (free tier covers 1000 RU/s per account).

## Still needed in Azure (#198)

- Check throughput on the shared free-tier database (≤ 1000 RU/s) and set an alert on 429s.
