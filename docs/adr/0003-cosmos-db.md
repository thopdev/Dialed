# ADR-0003: Azure Cosmos DB (NoSQL) on the free tier

**Status:** Accepted · 2026-11-05

## Context
The data is small per household, mostly read/written within one household, and needs change tracking for sync. Cost should be near zero during the beta.

## Decision
- **Azure Cosmos DB for NoSQL**, account created with **free tier** enabled (first 1000 RU/s and 25 GB free for the lifetime of the account; one free-tier account per subscription; must be chosen when creating the account; not available for serverless).
- One database with **shared throughput ≤ 1000 RU/s**, one container per entity type (max 25 containers on a free shared database).
- Partition by `/householdId` for household data (confirm in the Datasync spike).
- Data-plane access via **managed identity + Cosmos DB RBAC**, no keys.
- Local development and integration tests use the **Cosmos DB emulator**.

## Consequences
- Fits the household-scoped access pattern and gives a change feed for Functions.
- Relational-style queries (e.g. "all beans from roasters in Ethiopia used by my friends") must be designed per query or denormalised by change-feed Functions.
- Throughput is capped at 1000 RU/s to stay free; add an alert on throttled (429) requests.
- If `dev` and `prod` share a subscription, only one of them can be the free-tier account. Default for now: one subscription, with `dev` and `prod` as separate databases inside the single free-tier account (they share the 1000 RU/s).
- ASP.NET Core Identity (ADR-0010) needs a user store on Cosmos DB (custom stores or the EF Core Cosmos provider, decided in the passkey spike).

## Alternatives considered
- **Azure SQL (serverless)**: relational fit for households/friends; Datasync supports it well. Not chosen as the developer preferred Cosmos DB.
- **PostgreSQL Flexible Server**: small fixed monthly cost.
