# Architecture Decision Records

Short records of the decisions that shape Dialed. Format: Context → Decision → Consequences → Alternatives considered. Change a decision by adding a new ADR that supersedes the old one.

| ADR | Title | Status |
|---|---|---|
| [0001](0001-dotnet-maui-client.md) | .NET MAUI client for iPhone and iPad | Superseded by 0009 |
| [0002](0002-backend-hosting.md) | ASP.NET Core API on Container Apps + Azure Functions | Accepted |
| [0003](0003-cosmos-db.md) | Azure Cosmos DB on the free tier | Accepted |
| [0004](0004-offline-first-sync.md) | Offline-first sync with the Datasync Community Toolkit | Superseded by 0011 |
| [0005](0005-authentication.md) | Sign in with Apple + API-issued JWT | Superseded by 0010 |
| [0006](0006-ai.md) | Claude in Microsoft Foundry for label scanning and recipe help | Accepted — validate in spike |
| [0007](0007-infrastructure-as-code.md) | Bicep + Azure Developer CLI + GitHub Actions OIDC | Accepted |
| [0008](0008-images.md) | Images in Blob Storage with SAS upload | Accepted |
| [0009](0009-blazor-pwa-maui-ready.md) | Blazor WebAssembly PWA, structured to allow a MAUI app later | Accepted |
| [0010](0010-passkeys.md) | Passkeys with ASP.NET Core Identity, API-issued tokens | Accepted — validate in spike |
| [0011](0011-browser-offline-storage-and-sync.md) | SQLite in the browser + own sync engine on the Datasync server protocol | Accepted — validate in spike |
| [0012](0012-error-reporting.md) | Error reporting from the PWA with the Application Insights JavaScript SDK | Accepted |
| [0013](0013-friends-feed-at-read-time.md) | The friends feed is built when it is opened | Accepted |
