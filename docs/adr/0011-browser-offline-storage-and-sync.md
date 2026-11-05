# ADR-0011: SQLite in the browser + own sync engine on the Datasync server protocol

**Status:** Accepted — to be validated by the M0 spike · 2026-11-05 · Supersedes [ADR-0004](0004-offline-first-sync.md)

## Context
The app must be offline-first (log brews, add bags, edit recipes without a connection) across several people in a household. The Datasync Community Toolkit's offline client (`OfflineDbContext`) needs SQLite, which the toolkit does not support in Blazor WebAssembly. Its server side and its online HTTP protocol do work. The local data layer should be reusable by a future MAUI host (ADR-0009).

## Decision
- **Local database:** EF Core on **SQLite compiled to WebAssembly**, persisted in **OPFS** (fallback: IndexedDB), using **Bit.Bsqlite** or **SqliteWasmBlazor** — chosen in the spike. The `DbContext` lives in `Dialed.Client` and is provider-agnostic; a MAUI host would plug in native SQLite.
- **Server:** the **Datasync Community Toolkit server** in `Dialed.Api` (table controllers, `IAccessControlProvider<T>` for household scoping, soft delete, Cosmos DB repository). Its REST protocol is our sync wire protocol: filtered/paged queries, `updatedAt` watermarks, `__includedeleted`, `If-Match` versions.
- **Client sync engine (our own, in `Dialed.Client`):**
  - Every local change writes the entity **and** an **outbox** row in one transaction.
  - **Push:** outbox in order, with `If-Match: version`; 409/412 → conflict resolver.
  - **Pull:** per table and household, `updatedAt > watermark` including deleted rows, paged.
  - **Conflicts:** brews, inventory adjustments and label scans are append-only; editable entities use last-writer-wins with a local conflict log.
  - **One syncing tab** at a time via a cross-tab lock (Web Locks API).
  - Triggers: app start, coming back to the foreground, connectivity regained, after local changes (debounced), pull-to-refresh.
- **Storage can disappear** (iOS may clear website data): request persistent storage on every launch, push eagerly, and if the local database is missing on start, do a full re-sync. Unsynced changes older than a few minutes show a warning.
- The toolkit's client library may be used for its online HTTP operations if that fits; otherwise plain `HttpClient`.

## Spike (M0) must confirm
1. Bit.Bsqlite vs SqliteWasmBlazor on iOS Safari as an installed home-screen app: OPFS works, data survives app restarts, iOS updates and more than 7 days of not opening the app.
2. EF Core migrations on the client database.
3. Push/pull against the Datasync server with Cosmos DB (`/householdId` partitioning or the `/id` fallback) and the RU cost of a 1,000-item pull.
4. Cross-tab locking works in Safari.

## Consequences
- Full control and a data layer that is portable to MAUI, at the cost of writing and testing the sync engine ourselves.
- Both SQLite-WASM libraries are young; keep them behind the EF Core provider so they can be swapped, with IndexedDB (Dexie) as the last-resort fallback.
- The local database is a cache plus an outbox — the server is the source of truth.

## Alternatives considered
- **IndexedDB via JavaScript (Dexie)**: most proven browser storage, but the local data layer would have to be rewritten for MAUI.
- **Online-first with an offline queue**: simpler, but not offline-first.
- **Datasync offline client**: not supported in Blazor WebAssembly.
