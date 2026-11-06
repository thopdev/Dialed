# Dialed ☕

**Get every cup dialed in.** Dialed is an app for home baristas, made for iPhone and iPad: register beans, roasters and sellers, track what's left in the cupboard, keep recipes per brew method, log every brew, and share it all with your household and friends. AI reads coffee labels from a photo and suggests what to change in your next brew.

> Status: early development (M0 - Foundations). Start with the plan.

## Getting started

### Prerequisites (Windows, one-time)
1. [.NET 10 SDK](https://dotnet.microsoft.com/download) and VS Code with the recommended extensions (`.vscode/extensions.json`, mainly C# Dev Kit).
2. **Docker Desktop**, running (the Cosmos DB emulator and Azurite run as containers).
3. **Azure Functions Core Tools v4**: `winget install Microsoft.Azure.FunctionsCoreTools`
4. Trust the local HTTPS certificate: `dotnet dev-certs https --trust`
5. For testing on an iPhone/iPad only: the **dev tunnel CLI** - `winget install Microsoft.devtunnel`, then `devtunnel user login`.

### Run everything
Press **F5** in VS Code and pick **Dialed (Aspire)**, or:

```bash
dotnet run --project src/Dialed.AppHost
```

The Aspire dashboard opens (https://localhost:17000) with all resources, logs and traces. The first start downloads the emulator images and the Cosmos DB emulator needs a minute or two; both containers keep running between sessions, so later starts are quick.

| What | Where |
|---|---|
| App (PWA) | https://localhost:7200 - the home page shows "API: Healthy" when everything is connected |
| API | https://localhost:7100 (`/health`, `/alive`) |
| Cosmos DB Data Explorer, Azurite, Functions | links in the dashboard |

Passkeys (from #29/#30) work in the PC browser on `localhost`.

### Test on your iPhone or iPad
1. Start **Dialed (Aspire + dev tunnel for iPhone/iPad)** (or `dotnet run --project src/Dialed.AppHost --launch-profile tunnel`).
2. In the dashboard, open the `tunnel` resource and copy the public URL of **web** (`https://<tunnel>-7200.euw.devtunnels.ms`).
3. Open it in Safari on the iPhone. The app finds the API through the same tunnel automatically, and the API allows the tunnel origin.

The tunnel ID is derived from your AppHost, so the URL stays the same between runs (tunnels expire after 30 days without use). The tunnel allows anonymous access - only run it while you're testing.

### Build and test
```bash
dotnet build Dialed.slnx
dotnet test --solution Dialed.slnx     # xUnit v3 on Microsoft.Testing.Platform
```

### Demo data
In Development the AppHost sets `Seed__Enabled=true`; every `IDevelopmentSeeder` registered in the API then runs at startup (`src/Dialed.Api/Seeding`). Feature stories add their own seeders.

| Folder | What |
|---|---|
| `src/Dialed.AppHost` | Aspire AppHost: local orchestration (emulators, API, Functions, PWA, dev tunnel) |
| `src/Dialed.ServiceDefaults` | OpenTelemetry, health checks, service discovery, resilience for API and Functions |
| `src/Dialed.Web` | Blazor WebAssembly host (PWA) - browser-specific code only |
| `src/Dialed.UI` | Pages, components, layouts (shared with a future MAUI host) |
| `src/Dialed.Client` | Local database, sync engine, API clients, platform interfaces |
| `src/Dialed.Api` | ASP.NET Core API |
| `src/Dialed.Functions` | Azure Functions (copy `local.settings.example.json` to `local.settings.json`) |
| `src/Dialed.Domain` | Shared domain model and rules |
| `src/Dialed.AI` | Claude integration |
| `tests/` | xUnit v3 + Shouldly tests, incl. architecture rules |

## Documentation
- [Product & delivery plan](docs/PLAN.md): vision, scope, milestones, risks, open questions
- [Architecture](docs/architecture.md): components, domain model, sync, auth, AI, Azure resources, MAUI readiness
- [Architecture decisions (ADRs)](docs/adr/README.md)
- [Backlog](https://github.com/thopdev/Dialed/issues) and [milestones](https://github.com/thopdev/Dialed/milestones)

## Stack
| Layer | Technology |
|---|---|
| App | Blazor WebAssembly (.NET 10) as an installable PWA · Fluent UI Blazor · structured for a MAUI Blazor Hybrid app later |
| Offline | EF Core on SQLite in the browser (OPFS) · own sync engine |
| API | ASP.NET Core 10 on Azure Container Apps · Datasync server protocol |
| Background | Azure Functions (.NET 10 isolated, Flex Consumption) |
| Data | Azure Cosmos DB (NoSQL, free tier) · Azure Blob Storage |
| Auth | Passkeys (ASP.NET Core Identity) → API-issued JWT |
| AI | Claude in Microsoft Foundry (label photos, recipe suggestions) |
| Hosting & infra | Azure Static Web Apps · Bicep · Azure Developer CLI · GitHub Actions (OIDC) |
