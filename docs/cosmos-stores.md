# Cosmos DB stores in the API

How a store in `Dialed.Api` moves from memory to Cosmos DB (ADR-0003). The first one is the email recovery store (#197); the sync store (#38) and the identity stores follow the same pattern.

## The pieces (`src/Dialed.Api/Data/DialedCosmos.cs`)

| Piece | What it does |
| --- | --- |
| `CosmosConnection.From(configuration)` | Reads `ConnectionStrings:dialed` (the AppHost's database reference, which carries `Database=dialed`) or else `ConnectionStrings:cosmos`. Returns `null` when neither is set. |
| `CosmosConnection.CreateClient` | One client for the whole API: gateway mode for the local vNext emulator, System.Text.Json with camelCase names. |
| `DialedCosmos` | Singleton with the client and the database. `ContainerAsync(spec)` creates the database and container on first use (`CreateIfNotExists`), so startup never waits for Cosmos DB. |
| `CosmosContainerSpec(id, partitionKeyPath, defaultTtl)` | What a store needs. The default TTL of `-1` turns TTL on, and each document then sets its own `ttl`. |
| `services.AddCosmosOrInMemory<TService, TCosmos, TInMemory>(configuration)` | Registers the Cosmos DB store when Cosmos DB is configured, otherwise the in-memory one. |

## Adding a store

1. Keep the interface and the in-memory implementation. Tests, `WebApplicationFactory` apps and a bare `dotnet run` keep using it.
2. Write `CosmosXStore(DialedCosmos cosmos, ...)` with a static `CosmosContainerSpec`. Choose the partition key from how the store is read: everything one request needs should sit in one logical partition, so reads are point reads (1 RU). For example, recovery uses `/accountId` and household data uses `/householdId`.
3. Register it with `AddCosmosOrInMemory<...>(configuration)` in the feature's `AddDialed...` method.
4. Concurrency: read the document with its ETag and write with `IfMatchEtag`. On `412`/`409`, read again and retry a few times. Never read, change and write without the ETag.
5. Expiry: give documents that stop mattering a `ttl` in seconds (their window plus a margin). TTL runs on the server clock, so reads still compare against the injected `TimeProvider`.
6. Tests: write one abstract contract class with both a `...InMemory...Tests` and a `...Cosmos...Tests` subclass. Add Cosmos-only tests for what memory can't show, such as a new instance seeing the state or two instances racing. Cosmos tests call `Assert.SkipWhen(string.IsNullOrEmpty(COSMOS_CONNECTION_STRING), ...)` and use their own database, deleted afterwards.

## Running the Cosmos tests

CI's `cosmos` job starts the vNext emulator and runs `--filter-class "*Cosmos*"` on `Dialed.Api.Tests`. Locally:

```bash
docker run -d -p 8081:8081 mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-latest
```

```bash
COSMOS_CONNECTION_STRING="AccountEndpoint=http://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;" dotnet test --project tests/Dialed.Api.Tests --filter-class "*Cosmos*"
```

The key is the emulator's public, well-known key.

## Still needed in Azure (#198)

- In Azure the API connects with **managed identity and Cosmos DB data-plane RBAC**, not a key. `CosmosConnection` then needs an endpoint-only path, `new CosmosClient(endpoint, new DefaultAzureCredential())`, and the Bicep needs the role assignment.
- Containers are created by the app on first use. With RBAC the data-plane role can't create databases or containers, so in Azure they come from Bicep instead (`recovery`: partition `/accountId`, default TTL `-1`), and `DialedCosmos` must not try to create them.
- Check throughput on the shared free-tier database (≤ 1000 RU/s) and set an alert on 429s.
