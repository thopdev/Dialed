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

## Stores on Cosmos DB

| Store | Container | Partition key | TTL |
| --- | --- | --- | --- |
| `CosmosAiQuotaStore` (AI calls per user, operation and UTC day) | `aiquota` | `/userId` | each day's counter: 2 days |
| `CosmosCatalogStore`, `CosmosCatalogRulesStore` (catalog build in parts, hide and merge rules) | `catalog` | `/pk` (`build`, `rules`) | none |
| `CosmosEmailRecoveryStore` | `recovery` | `/accountId` | per document |
| Identity stores (see below) | `identity` | `/pk` | per document |
| `CosmosPushStore` (devices, switches, stock alert memory, one-push-per-event markers) | `push` | `/userId` | event markers: 1 hour and a minute |

Each container also has a block in `infra/modules/cosmos-database.bicep` (alphabetical). Cosmos test classes share `CosmosTestDatabase` (a throwaway database per test class instance).

## The identity stores (#38)

Accounts, passkeys and sign-ins live in the `identity` container (partition key `/pk`, default TTL `-1`), as ADR-0010 planned. Every document has `pk = id` and a `type`, so each key is its own logical partition and every lookup by key is a point read; a document is written on its own with its ETag. One container instead of one per store, because none of these stores needs a multi-document transaction and they share the database's throughput.

| Document id | Store | What it holds | Expires |
| --- | --- | --- | --- |
| `user:{userId}` | `CosmosUserStore` | The Identity user with its passkeys and recovery code hashes. Saved with its ETag; a stale save is a concurrency failure. | never |
| `email:{sha256(normalized email)}` | `CosmosUserStore` | Points to the user. Creating it claims the address (409: taken), so emails stay unique across instances. Hashed to keep emails out of ids. | never |
| `passkey:{sha256(credential id)}` | `CosmosUserStore` | Points to the user, for passkey sign-in. Hashed because a credential id can be longer than an id may be (1023 bytes). | never |
| `profile:{userId}` | `CosmosUserDirectory` | Display name, email, active household and other households (`households` holds all of them for the members query). | never |
| `refresh:{sha256(token)}` | `CosmosRefreshTokenStore` | User, token family, expiry, used and revoked flags. Rotation marks it used with the ETag, so two refreshes with one token cannot both win. | after its expiry |
| `ceremony:{sha256(nonce)}` | `CosmosCeremonyNonces` | A claimed ceremony nonce: creating it claims it, a replay gets 409. | after the ceremony lifetime |
| `invite:{sha256(CODE)}` | `CosmosBetaInviteStore` | A beta invite with its uses, counted with the ETag. | a day after it expires |

- **Lookups are written before the user and removed after it**, and a lookup only counts when the user it points to still has that email or passkey. The lookup and the user sit in different partitions, so they cannot share a transaction: a claim younger than 5 minutes (`CosmosUserStore.OrphanedClaimAge`) always counts as taken, because its sign-up may still be writing the user. A crash between two writes leaves a harmless orphan, which a later sign-up takes over with its ETag once it is older than that.
- **Queries** (cross-partition, small): the members of a household (`ARRAY_CONTAINS(c.households, @id)`), and revoking a token family or all of a person's tokens (sign-out, a reused token, account deletion). The hot paths (refresh, sign-in, `/auth/me`) are point reads.
- **Development users**: `/auth/dev` signs in as alice, bob or carol. With Cosmos DB in Development the directory creates their profiles on first use (create only, so a changed household stays).
- **Passkeys depend on these documents**: deleting `user:` documents or changing the RP ID invalidates the passkeys on people's devices. Never clear the `identity` container of an environment with real users.

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
- Containers are created by the app on first use. With RBAC the data-plane role can't create databases or containers, so in Azure they come from Bicep instead (`recovery`: partition `/accountId`, default TTL `-1`; `identity`: partition `/pk`, default TTL `-1`), and `DialedCosmos` must not try to create them.
- Check throughput on the shared free-tier database (≤ 1000 RU/s) and set an alert on 429s.
