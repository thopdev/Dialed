# Dialed: notes for Claude Code

Dialed is a coffee app: a Blazor WebAssembly PWA (Fluent UI Blazor, .NET 10, MAUI-ready) with an ASP.NET Core API. Owner: Thomas (Dutch speaker, writes English; wants concise, structured, actionable answers and options before big plans). Read `CONTRIBUTING.md`, `docs/architecture.md` and `docs/adr/` for background.

## Build, test, run
- `dotnet build Dialed.slnx` and `dotnet test --solution Dialed.slnx` (xUnit v3 + Shouldly on Microsoft.Testing.Platform; bUnit for screens). Warnings are errors, so a clean build matters.
- Run the app: `dotnet run --project src/Dialed.AppHost` (Aspire) or the Web and Api projects separately. Development host validates DI scopes. `/auth/dev` logs in as alice, bob or carol in Development. With `Seed__Enabled=true` (set by the AppHost) the API fills alice and bob's household with demo beans, bags, recipes and brews (`DemoDataSeeder`).
- `/dev/gallery` shows every component in light/dark. Check UI work at iPhone width (about 390 px) and iPad, in light and dark mode.

## Architecture rules (enforced by `tests/Dialed.ArchitectureTests`)
- `Dialed.Domain`: pure logic, no other project. `Dialed.Client` and `Dialed.UI`: no browser APIs (no `Microsoft.JSInterop`); platform things are interfaces in `Dialed.Client.Platform` (add new ones to the architecture test list), implemented in `Dialed.Web` (JS in `wwwroot/js/platform.js`).
- Screens talk to `IRepository<T>`, never to storage directly. Sync entities derive from `SyncEntity`.
- Azure is not available: build against interfaces with in-memory fakes (email sender, AI client, storage); Azure-only work is filed as an issue and documented, not done.

## UI conventions
- Every UI string goes in **both** `src/Dialed.UI/Resources/Strings.resx` and `Strings.nl.resx` (parity is tested). Append new entries before `</root>`; on merge conflicts keep both sides.
- Colours only from Fluent `--color*` variables; spacing and type from `tokens.css` (`--dialed-*`). Touch targets at least 44 px, sizes in `rem`. See `docs/accessibility.md`: every control needs an accessible name; add new screens to `AccessibilityTests`.
- Reuse components in `src/Dialed.UI/Components` (Card, ListRow, Chip, EmptyState, Avatar, Thumb, StatTile, Meter, SectionHeader...).
- Test ids: screens use stable `id`s (`#scan-save`, `#settings-feedback`) that UI tests rely on.

## Working agreements
- One PR per story against `main`, branch from fresh `main` (`feat/<issue>-<slug>`), no stacked PRs, up to 10 open PRs. Commit style: Conventional Commits with the issue number. `Closes #N` in the PR. Thomas reviews and merges; if `main` moved and the PR conflicts, merge `main` into the branch and fix (for `.resx` and `components.css` keep both sides).
- Fix CI failures and merge conflicts without being asked. Do not ask for iPhone testing you can do yourself; list real-device checks (wake lock, silent switch, storage eviction, passkeys on device) as manual steps in the PR.
- Do not request computer-use access. Do not enter credentials anywhere.
- User-only items: merging PRs, branch protection, Dependabot decisions, Azure setup, iPhone checks.
- Commits and PRs end with the attribution lines the session provides.
- UI PRs show what changed: a "Screenshots" section in the PR description with before (current `main`) and after at iPhone width (about 390 px), light mode, plus a short GIF for anything interactive. Dark mode and iPad go in one collapsed sheet. Keep the images on the code-free `pr-media` branch (one folder per PR, never merged) and link them with `?raw=true`.

## Where things stand
- Waves 0 to 3 done (core app, accounts, households with create/rename/delete and multi-use invites, friends with copy and an activity feed, sync with a cross-tab lock, passkeys, security, error reporting including JavaScript errors, accessibility pass, install metadata, privacy page, feedback, statistics, CSV import).
- Photos: upload queue with short-lived URLs (#68), metadata stripping and thumbnails (#69), offline label scans read by photo id (#83).
- AI features run on offline rules behind the kill switch and quotas (`IDialInRefiner`, `IRecipeSuggester`, `ILabelExtractor` with fakes): label scan, dial-in advice and assistant, starting recipe, acceptance metrics per prompt version. The real Claude calls need Azure (#27, #79, #81).
- Visual polish (#213 to #216, on the shared components of #217) is done and merged; preferences and finished setup are saved on the device (`StoredUserPreferences`).
- Issue hygiene: an issue whose non-Azure scope is merged but has Azure or device work left stays open with a "Status" comment listing what is left. Use `Part of #N` in PRs that do not finish an issue, and `Closes #N` only when every acceptance criterion is met.
- Maps and insights (round 2, all merged; `docs/insights.md`): offline origin maps (world, per country zoom, tap to filter Beans, friend profiles), countries-tried passport, flavour wheel, brewing calendar, milestones, dial-in trace, compare two brews of a bean, brew card (`IImageRenderer`, new platform interface), peak heads-up push. The map outlines are generated by `tools/map-data` (not part of the build). The service worker now really caches the app (`ServiceWorkerAssetsManifest`, #283): real-device offline checks are in `docs/iphone-checks.md`.
- Open, no Azure needed: nothing planned (the post-MVP stories are built). Docs for the beta are in `docs/beta-testing.md`, the real-device list is `docs/iphone-checks.md`, the native app estimate is `docs/native-app.md`.
- Open, owner: real-iPhone checks for barcode, push, public profiles, catalog (lists in `docs/iphone-checks.md`), `Feedback:Email` in `src/Dialed.Web/wwwroot/appsettings.json` (needs the address), #99 beta testers, real-device checks (#37, #49), the 30 real label photos for #84, and whether to close #110 (its sign-in test is merged; running against preview environments needs #22).
- Open, Azure: #22, #24 to #28, #38 (Cosmos), #43 (timer Function), #79, #81, #88 and #90 (Application Insights), #111, #112 (email via #199), #198, #199, #260, #268.
- Post-MVP (P2) built and merged: barcode scan (#104, `IBarcodeScanner`, zxing-wasm self-hosted), push notifications (#100 follow and household events, #259 low stock and peak alerts; `IPushNotifications`, VAPID keys from `Push:*`, hosted `StockAlertWorker`), public profiles with a fourth visibility `Public` (#106; reports and suspension with the admin key, `docs/public-profiles.md`) and the shared bean and roaster catalog (#105, needs 2 different people per entry, `docs/catalog.md`). Docs: `docs/push-notifications.md`, `docs/catalog.md`. Still open: Azure parts #260 (push keys, Cosmos subscription store, timer Function) and #268 (catalog rebuild Function and Cosmos stores), and #261 (native extras: widgets, Shortcuts, Watch) which waits for a native app (`docs/native-app.md`); it replaces #102 and #103.
