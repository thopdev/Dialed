# ADR-0007: Bicep + Azure Developer CLI, GitHub Actions with OIDC

**Status:** Accepted · 2026-11-05

## Context
Everything should be hosted on Azure and reproducible; two environments (`dev`, `prod`).

## Decision
- **Bicep** modules in `/infra`, orchestrated by the **Azure Developer CLI** (`azure.yaml`, `azd provision`, `azd deploy`).
- **GitHub Actions** authenticates to Azure with **OIDC federated credentials** (no client secrets in GitHub).
- **Managed identities + RBAC** between services; secrets only in **Key Vault**.
- `dev` deploys on every merge to `main`; `prod` deploys from a release tag with a manual approval (GitHub environment protection).
- A **budget alert** is part of the infra.

## Consequences
- Azure-native, no state file to manage (unlike Terraform).
- `azd` gives one command for local "provision + deploy" too.

## Alternatives considered
- **Terraform**: multi-cloud, needs remote state.
- **Portal/manual**: not reproducible.
