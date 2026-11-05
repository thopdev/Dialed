# ADR-0002: ASP.NET Core API on Azure Container Apps + Azure Functions for background work

**Status:** Accepted · 2026-11-05

## Context
The first preference was an Azure Functions–only backend. The app must be offline-first, and the most mature .NET offline sync library, the Datasync Community Toolkit (ADR-0004), is built on ASP.NET Core Web API controllers (`TableController<T>`), which do not run inside Azure Functions.

## Decision
- **Dialed.Api**: ASP.NET Core 10 Web API in a container on **Azure Container Apps** (Consumption, scale to zero). Hosts auth, Datasync table controllers, households, friends, AI endpoints and SAS issuing.
- **Dialed.Functions**: **Azure Functions** (.NET 10 isolated worker, **Flex Consumption**) for event-driven and scheduled work: thumbnails/EXIF stripping on blob events, Cosmos DB change-feed processing, cleanups, invite expiry.

## Consequences
- Use the ready-made sync library instead of building a sync protocol.
- Two deployables instead of one; both share `Dialed.Domain` and are deployed by `azd`.
- Cold starts on scale-to-zero (a few seconds on the first request) — acceptable for a beta; set min replicas to 1 in prod if it annoys.
- Needs a container registry.

## Alternatives considered
- **Functions only + custom sync protocol**: one hosting model, but sync, delta tokens, conflicts and the offline client would all be custom code.
- **App Service** for the API: always-on cost; Container Apps scales to zero.
