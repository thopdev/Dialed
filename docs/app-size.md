# App size and speed

A snapshot from 2026-11-12, after the round-3 features. Re-measure when something big lands; the numbers are for the first load of a Release build (everything is brotli-compressed on the wire).

## How to measure
```bash
dotnet publish src/Dialed.Web -c Release -o /tmp/pub
cd /tmp/pub/wwwroot && find . -name '*.br' | xargs ls -lS | head -20
```
The `.br` files are what the browser downloads. The service worker caches them, so only the first visit and updates pay.

## Size
| Part | Brotli | Notes |
|---|---|---|
| `dotnet.native.wasm` (runtime) | 977 KB | |
| `Microsoft.EntityFrameworkCore` + `.Relational` | 694 KB + 568 KB | pulled in by the local SQLite store (Bit.Bsqlite); the biggest single dependency |
| `System.Private.CoreLib` | 676 KB | |
| `System.Private.Xml`, `System.Data.Common` | 363 KB, 182 KB | also from the data layer |
| wa-sqlite engine | 258 to 374 KB | three variants are published, a browser downloads one |
| zxing barcode reader | 321 KB | only fetched when the barcode scanner opens |
| `Dialed.UI` | 322 KB | was about 312 KB before this round of features |
| `Dialed.Client` | 131 KB | |
| `Dialed.Domain` | 97 KB | |
| Dutch strings | 29 KB | satellite assembly, fetched for Dutch only |
| Everything published (all `.br` files) | 7.5 MB | raw 27 MB; includes files a given browser never fetches |

Our own code (UI, Client, Domain, Web) is about 0.6 MB of that. A whole round of features (bean card, journey, gear upkeep and push, drift, suggestions, recipe from best brews, taste profile, water) added roughly 10 KB to `Dialed.UI`, so the app is not growing from features; the weight is the runtime and the data layer.

If load time ever needs work, the candidates in order of payoff are the EF Core pair (replace the data layer with plain SQLite calls, a big change), Brotli-level and trimming checks on the data-layer assemblies, and lazy loading of rarely used screens (map data is about 100 KB of that UI assembly).

## Speed of the derived views
The Domain code behind the insight screens, on a made-up household of 200 beans, 600 bags and 8,000 brews (about seven years of a daily habit), run natively on a build server (the browser runs the same code under WebAssembly, which is several times slower than these numbers):

| View | Time |
|---|---|
| Statistics | 34 ms |
| Inventory overview (Home) | 6 ms |
| Dial-in trace (all brews) | 3 ms |
| Bean journey, best-brew recipe, grind drift, taste profile (one bean or bag) | under 3 ms each |

Nothing here is a problem for a normal household (a few hundred brews). Statistics is the first thing to cache or compute incrementally if someone imports years of history.
