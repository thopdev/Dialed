# Dialed ☕

**Get every cup dialed in.** Dialed is an iPhone and iPad app for home baristas: register beans, roasters and sellers, track what's left in the cupboard, keep recipes per brew method, log every brew, and share it all with your household and friends. AI reads coffee labels from a photo and suggests what to change in your next brew.

> Status: planning. Nothing is built yet. Start with the plan.

## Documentation
- [Product & delivery plan](docs/PLAN.md): vision, scope, milestones, risks, open questions
- [Architecture](docs/architecture.md): components, domain model, sync, auth, AI, Azure resources
- [Architecture decisions (ADRs)](docs/adr/README.md)
- [Backlog](https://github.com/thopdev/Dialed/issues) and [milestones](https://github.com/thopdev/Dialed/milestones)

## Stack
| Layer | Technology |
|---|---|
| App | .NET MAUI (iOS / iPadOS) · MVVM · SQLite offline store |
| Sync | Datasync Community Toolkit (offline-first) |
| API | ASP.NET Core 10 on Azure Container Apps |
| Background | Azure Functions (.NET 10 isolated, Flex Consumption) |
| Data | Azure Cosmos DB (NoSQL, free tier) · Azure Blob Storage |
| Auth | Sign in with Apple → API-issued JWT |
| AI | Apple Vision OCR on device · Claude in Microsoft Foundry |
| Infra | Bicep · Azure Developer CLI · GitHub Actions (OIDC) |
