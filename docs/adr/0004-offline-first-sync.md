# ADR-0004: Offline-first sync with the Datasync Community Toolkit

**Status:** Superseded by [ADR-0011](0011-browser-offline-storage-and-sync.md) · 2026-11-05 (the Datasync *server* is still used; its offline *client* doesn't support Blazor WebAssembly)

## Context
All of a user's household data must work without a connection (logging brews, adding bags, editing recipes) and sync later, across several people in the same household.

## Decision
Use the **Datasync Community Toolkit** (.NET Foundation):
- **Server**: `TableController<T>` per synced entity in `Dialed.Api`, Cosmos DB repository, `IAccessControlProvider<T>` for household scoping, soft delete enabled.
- **Client**: `OfflineDbContext` (EF Core + SQLite) in the MAUI app; `PushAsync` / `PullAsync` with delta tokens; bearer token via the toolkit's authentication provider.
- **Conflicts**: append-only design for brews, inventory adjustments and label scans; `ClientWinsConflictResolver` (last writer wins) for editable entities, logging the overwritten version.
- **Inventory** is derived (bag weight − brews − adjustments), never a shared counter.
- Friend content is **not** synced; it is read online.

See [architecture.md §3](../architecture.md#3-offline-first-sync).

## Spike (M0) must confirm
1. Cosmos DB repository with `/householdId` partitioning (or the fallback: partition on `/id`, cross-partition queries at beta scale).
2. `GetDataView()` filtering on "households the user belongs to" with delta-token pulls.
3. MAUI iOS client: SQLite, push/pull on resume and connectivity change, behaviour with app backgrounding.
4. Datasync client entities need `Id`, `UpdatedAt` (store `DateTimeOffset` with a value converter; SQLite defaults to second accuracy), `Version`, `Deleted`.

## Consequences
- Far less custom code than a hand-written sync protocol; well-documented server + client.
- Requires the ASP.NET Core API host (ADR-0002).
- Local DB is the source of truth for the UI → UI must handle "not yet synced" states.

## Alternatives considered
- **Custom delta sync on Azure Functions** (outbox + change tokens): full control, much more work and risk.
- **Online-only with cache**: simpler, but doesn't meet the offline-first requirement.
