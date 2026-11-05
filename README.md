# Dialed ☕

**Get every cup dialed in.** Dialed is an app for home baristas, made for iPhone and iPad: register beans, roasters and sellers, track what's left in the cupboard, keep recipes per brew method, log every brew, and share it all with your household and friends. AI reads coffee labels from a photo and suggests what to change in your next brew.

> Status: early development (M0 - Foundations). Start with the plan.

## Development
Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download) and VS Code with the recommended extensions (`.vscode/extensions.json`, mainly C# Dev Kit). Emulators, dev tunnel and one-command startup follow in #20.

```bash
dotnet build Dialed.slnx
dotnet test --solution Dialed.slnx     # xUnit v3 on Microsoft.Testing.Platform
dotnet run --project src/Dialed.Web    # the PWA (placeholder page for now)
dotnet run --project src/Dialed.Api    # the API, health check at /health
```

| Folder | What |
|---|---|
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
