# ADR-0007: Bicep + Azure Developer CLI, GitHub Actions with OIDC

**Status:** Accepted · 2026-11-05 (revised the same day: Static Web Apps, custom domain and email added)

## Context
Everything should be hosted on Azure and reproducible; two environments (`dev`, `prod`).

## Decision
- **Bicep** modules in `/infra`, orchestrated by the **Azure Developer CLI** (`azure.yaml`, `azd provision`, `azd deploy`).
- Resources include Azure Static Web Apps (PWA), Container Apps (API), Functions, Cosmos DB, Storage, Key Vault, Azure Communication Services Email, Foundry, monitoring — see [architecture.md §7](../architecture.md#7-azure-resources-per-environment).
- **Region**: West Europe.
- **Custom domain (prod, before the beta)**: `app.<domain>` → Static Web Apps, `api.<domain>` → Container Apps (DNS at the registrar or in Azure DNS); it is the passkey RP ID for prod (ADR-0010). `dev` uses Azure's default hostnames.
- **GitHub Actions** authenticates to Azure with **OIDC federated credentials** (no client secrets in GitHub). Static Web Apps creates a **preview environment per pull request**.
- **Managed identities + RBAC** between services; secrets only in **Key Vault**.
- `dev` deploys on every merge to `main`; `prod` deploys from a release tag with a manual approval (GitHub environment protection).
- A **budget alert** is part of the infra.

## Consequences
- Azure-native, no state file to manage (unlike Terraform).
- `azd` gives one command for local "provision + deploy" too.

## Alternatives considered
- **Terraform**: multi-cloud, needs remote state.
- **Portal/manual**: not reproducible.
