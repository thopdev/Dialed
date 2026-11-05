# Dialed ☕

**Get every cup dialed in.** Dialed is an app for home baristas, made for iPhone and iPad: register beans, roasters and sellers, track what's left in the cupboard, keep recipes per brew method, log every brew, and share it all with your household and friends. AI reads coffee labels from a photo and suggests what to change in your next brew.

> Status: planning. Nothing is built yet. Start with the plan.

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
