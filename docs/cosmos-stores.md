# Cosmos DB stores in the API

How a store in `Dialed.Api` moves from memory to Cosmos DB (ADR-0003). The first one is the email recovery store (#197); the sync, household, friend and public profile stores (#38) and the identity stores follow the same pattern.

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

## Sync, households, friends and public profiles (#38)

| Container | Partition key | Store | Documents | TTL |
| --- | --- | --- | --- | --- |
| `sync` | `/householdId` | `CosmosSyncStore` | one per record, id `{table}:{id}`; the app's JSON under `entity` (not indexed), the fields queries use copied next to it | soft-deleted records: 37 days (purge window + a week) |
| `households` | `/householdId` | `CosmosHouseholdStore` | `household` and `invite-{CODE}` in the household's partition | invites: until they expire + 1 minute |
| `friends` | `/pk` | `CosmosFriendStore` | `user:{id}` holds `code`, `follow:{followee}`, `block:{blocked}`; `code:{CODE}` points to its person | none |
| `publicprofiles` | `/userId` | `CosmosPublicProfileStore` | `profile` and `report-{hash}` (reports about the person) | none |

- **Pulls** order by `sortKey` (update time in ticks, zero-padded, then the record id), so the watermark is one range filter on one indexed field and needs no composite index. Someone else's private records are filtered in the query, so pages stay full.
- **Pushes** read the record and write it back with its ETag (create when new); a write that lost the race reads again, which ends in the usual version mismatch. Two instances pushing the same version: one wins.
- **Cross-partition reads**, only where the key is unknown: `ListPublicAsync` (catalog build), the daily purge, looking up an invite code on joining, who follows a person, the open and suspended profile lists and the reports list (moderation), and account deletion.
- **Purge**: `SyncPurgeWorker` works as before (`deleted = true AND ts < cutoff`, across households). The TTL on deleted records is a backstop in case the worker does not run.
- **Development**: the dev households (`dev-household`, `dev-household-carol`) are registered as `Household` services and both stores start with them; the Cosmos store creates them once and never overwrites a renamed one.
- **Indexing**: the `sync` entry in Bicep excludes `/entity/*`. Containers the app creates itself (the emulator) index everything, which only costs RU.

### Expected RU (estimates, 1 KB records, `/entity/*` not indexed)

| Operation | RU |
| --- | --- |
| Push of a new record (point read + create) | about 8 |
| Push of a change (point read + replace with ETag) | about 11 |
| Pull of one table with nothing new (single-partition query) | about 3 |
| Pull of a page of 100 changes | about 10 to 15 |
| A full sync with nothing new (9 tables) | about 27 |
| Shared list of one person (friend profile, feed) | about 3 + 0.3 per record read |

At 400 RU/s shared by every container, that is roughly 15 idle syncs or 35 pushes per second, plenty for the beta. The SDK retries 429s itself; the alert on 429s (#198) tells when to raise throughput. Indexing `entity` as well would roughly double the cost of a push.

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

To add one, add a comment line and an entry line at its alphabetical place by `id`, matching the store's `CosmosContainerSpec` (`defaultTtl: -1` turns TTL on with a per-document `ttl`; leave `defaultTtl` out for no TTL). An entry may also carry an `indexingPolicy` (as `sync` does to skip `/entity/*`). Different PRs then touch different lines; on a merge conflict keep both entries. The next `azd provision` (the deploy workflow runs it when `infra/` changed) creates the container. Never change the partition key of an existing container: Cosmos DB can't, the deployment fails, and the data has to be moved to a new container.

At startup in Azure, `CosmosStartupCheck` lists the database's containers and does one point read in each. It logs `Cosmos DB reachable at ..., containers: ...` or, as an error, `Cosmos DB not reachable` (usually a missing role assignment). It only logs; `/health` does not depend on Cosmos DB. To find it: `az containerapp logs show -g rg-dialed-dev -n ca-dialed-api-dev --tail 200 | grep "Cosmos DB"`.

All containers share the database's 400 RU/s (free tier covers 1000 RU/s per account).

## Still needed in Azure (#198)

- Check throughput on the shared free-tier database (≤ 1000 RU/s) and set an alert on 429s.
