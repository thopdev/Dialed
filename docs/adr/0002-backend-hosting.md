# ADR-0002: ASP.NET Core API on Azure Container Apps + Azure Functions for background work

**Status:** Accepted · 2026-11-05 (revised the same day for the PWA client, see ADR-0009/0010/0011)

## Context
The first preference was an Azure Functions–only backend. The app must be offline-first, and the sync server we use, the Datasync Community Toolkit server (ADR-0011), is built on ASP.NET Core Web API controllers (`TableController<T>`), which do not run inside Azure Functions. Passkey sign-in uses ASP.NET Core Identity (ADR-0010), which also expects a regular ASP.NET Core host.

## Decision
- **Dialed.Api**: ASP.NET Core 10 Web API in a container on **Azure Container Apps** (Consumption, scale to zero), at `api.<domain>`. Hosts Identity + passkey endpoints and token issuing, Datasync table controllers, households, friends, AI endpoints and SAS issuing.
- **Dialed.Functions**: **Azure Functions** (.NET 10 isolated worker, **Flex Consumption**) for event-driven and scheduled work: thumbnails/EXIF stripping on blob events, Cosmos DB change-feed processing, cleanups, invite expiry.
- The PWA itself is static files on Azure Static Web Apps (ADR-0009).

## Consequences
- Use a ready-made sync server instead of designing a sync protocol from scratch.
- Two deployables (API, Functions) plus the static app; all share `Dialed.Domain` and are deployed by `azd`.
- Cold starts on scale-to-zero (a few seconds on the first request) — acceptable for a beta; set min replicas to 1 in prod if it annoys.
- Needs a container registry and CORS configuration for the app's origin.

## Alternatives considered
- **Functions only + custom sync protocol**: one hosting model, but sync and auth would be fully custom.
- **App Service** for the API: always-on cost; Container Apps scales to zero.
