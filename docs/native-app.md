# A native iOS app with MAUI Blazor Hybrid (#109)

A decision aid, not a plan. The PWA is the product for the private beta (ADR-0009); this page says what a native app would cost, what it needs and what it would add, so the choice can be made later with numbers.

## What carries over unchanged
`Dialed.UI` (all screens), `Dialed.Client` (data, sync, queues) and `Dialed.Domain` are free of browser APIs, and architecture tests keep them that way. A MAUI Blazor Hybrid host reuses them as they are; only the platform interfaces and the storage provider are native. The step-by-step list is in [architecture.md §10](architecture.md#10-maui-readiness).

## Effort estimate
Rough, from reading the code, not from building it. Assumes one developer who already has a Mac and an Apple developer account, and counts working days of focused effort.

| Piece | Days | Notes |
|---|---|---|
| `Dialed.Maui` host project, routes and layout reused, app icon and splash | 2 to 3 | Fluent UI Blazor already works in MAUI Blazor Hybrid |
| Ten platform interfaces, native (`IPhotoCapture`, `ILocalFileStore`, `IConnectivity`, `IShareService`, `IStoragePersistence`, `IWakeLock`, `ISoundCue`, `IAppLifecycle`, `ICrossTabLock`, `IPasskeyClient`) | 5 to 8 | Nine are a few lines over MAUI Essentials; the passkey client is the hard one (see below) |
| Passkeys with `ASAuthorization` and Associated Domains for the API's domain | 3 to 5 | Needs the custom domain first (#111): the RP ID is bound to it |
| Native SQLite provider for the local store instead of the browser one | 3 to 5 | The store is behind `ILocalStore`; the contract tests tell when it is right |
| Photo uploads, background behaviour, offline queues on a real device | 3 to 5 | The queues exist; what changes is that iOS suspends the app |
| Device testing on iPhone and iPad, accessibility pass, fixes | 5 to 8 | Safe areas, keyboard, VoiceOver |
| TestFlight and App Store set-up, privacy labels, review rounds | 3 to 5 | Mostly waiting; plan for a rejection |
| **Total to a first TestFlight build** | **about 25 to 40 days (5 to 8 weeks)** | Treat the low end as a best case |

After that, each release adds the App Store review to the loop, and two code paths (PWA and native) have to be tested.

## Prerequisites
- Apple Developer Program (99 USD per year).
- A Mac for builds and signing, or a cloud Mac (the .NET MAUI iOS workload needs Xcode).
- App Store Connect: the app record, TestFlight testers, privacy nutrition labels, a privacy policy URL (the page in the app already exists), support URL.
- The custom domain and API from #111, and the `webcredentials:` Associated Domains entry for passkeys.
- Test devices (an iPhone and an iPad).
- CI for iOS builds (a macOS runner, or manual builds at first).

## What it would add
| Capability | Value for Dialed | Needs |
|---|---|---|
| Haptics | Tactile cue for timer steps (the PWA only has sound) | MAUI `HapticFeedback` |
| On-device text recognition (Vision) | Read labels without a server, so offline scans finish at once and cost no quota | A native `ILabelExtractor`; the offline queue from #83 stays as fallback |
| Reliable push | Invites, follow requests, low stock, a bag reaching its peak (#100) | APNs and a server sender; Web Push in an installed PWA works but is less reliable on iOS |
| Home-screen widgets | "Log my usual" and stock at a glance (#102) | A WidgetKit extension in Swift next to the MAUI app |
| Siri Shortcuts | Log a brew by voice (#102) | App Intents, also native Swift |
| Apple Watch | Timer on the wrist (#103) | A separate watchOS app; needs this native app first |
| Storage that is not evicted | No "7 days unused" browser rule | Native app storage is persistent |
| App Store presence | Discoverability | Review and fees |

## What it would cost beyond the build
- Two shells to keep working: browser-only features (service worker, Web Locks) have native twins.
- Slower releases: the PWA updates on deploy; native needs review.
- Widgets, Shortcuts and the Watch are Swift, outside the .NET code base.

## Recommendation
Not yet. The beta is aimed at friends and household, and the PWA already installs, works offline and syncs. Decide after the beta, and only when one of these happens: testers lose data to browser storage eviction in practice, push notifications matter to them, or someone asks for the Watch or widgets. Until then, keep the architecture tests green, because they are what makes the native shell a few weeks of work instead of a rewrite.
