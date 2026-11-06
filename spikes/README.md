# Spikes

Throwaway prototypes that answer one question each. Not part of `Dialed.slnx`; built by the `Spikes` workflow.

## #37 - SQLite in the browser (Bit.Bsqlite vs SqliteWasmBlazor)

Two identical Blazor WebAssembly apps that share one EF Core model (`StorageSpike.Shared`): brews, an outbox and a launch log, with a hand-written EF Core migration. Each app records every launch, so you can see whether the data is still there days later.

| App | Library | Port | Build needs |
|---|---|---|---|
| `StorageSpike.Bsqlite` | Bit.Bsqlite 10.7.0 (OPFS, IndexedDB fallback) | https://localhost:7301 | nothing extra |
| `StorageSpike.SqliteWasm` | SqliteWasmBlazor 0.9.4-pre (OPFS) | https://localhost:7302 | `dotnet workload install wasm-tools` |

### Run on the PC
```bash
dotnet run --project spikes/StorageSpike.Bsqlite
dotnet run --project spikes/StorageSpike.SqliteWasm
```

### Run on the iPhone / iPad (the part that matters)
1. Start both apps (above).
2. One-time: `devtunnel create dialed-spike --allow-anonymous`, then `devtunnel port create dialed-spike -p 7301 --protocol https` and the same for `7302`.
3. `devtunnel host dialed-spike` - it prints one URL per port.
4. Open each URL in Safari, **Share > Add to Home Screen**, and open it from the home screen.
5. In each app: tap **Insert 5,000 brews**, **Query**, **Update 100**, **Web Lock**; note the timings.
6. Close the apps. Reopen after a day, after an iOS update if one comes, and after **8+ days without opening them**. "Launches recorded", "First launch" and "Brews stored" show whether the data survived.

Note the results in issue #37; they decide [ADR-0011](../docs/adr/0011-browser-offline-storage-and-sync.md).

Both libraries also have public demos you can install to the home screen right away: the [SqliteWasmBlazor demo](https://b-straub.github.io/SqliteWasmBlazor/) and the Bit.Bsqlite demo at bsqlite.bitplatform.dev.
