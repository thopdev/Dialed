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

## Where things stand
- Waves 0 to 3 done (core app, accounts, households, friends, sync, passkeys, security, error reporting, accessibility pass, install metadata, privacy page, feedback, statistics, CSV import, label scan with a fake AI client, AI quotas).
- Open: visual polish stories #213 (Home/Brew), #214 (Beans/Bags/Inventory), #215 (Recipes/History), #216 (Settings/More/People/Onboarding), built on the shared components of #217. Also pending: `Feedback:Email` in `src/Dialed.Web/wwwroot/appsettings.json` (owner has to give the address), offline scan queue (needs #68), JS-level error reporting, Azure items (#25, #79, #84, #198, #199).
