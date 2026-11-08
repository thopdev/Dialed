# Infrastructure (Bicep + azd)

Everything Azure for one environment is defined here (ADR-0007, [architecture §7](../docs/architecture.md#7-azure-resources-per-environment)).

```
azd env new dev            # once per environment
azd provision              # creates rg-dialed-dev and everything in it
azd deploy                 # builds and ships the API (Container Apps) and Functions
```

`azure.yaml` is the azd project, `infra/main.bicep` the entry point (subscription scope), `infra/modules/` one module per resource group of concerns.

## What gets created

| Module | Resources |
|---|---|
| `monitoring` | Log Analytics `log-dialed-{env}`, Application Insights `appi-dialed-{env}` |
| `identity` | `id-dialed-api-{env}`, `id-dialed-func-{env}` (user-assigned managed identities) |
| `staticwebapp` | `swa-dialed-{env}` (Free); the PWA is deployed by the Static Web Apps action (#22) |
| `keyvault` | `kv-dialed-{env}-{token}` (RBAC; Secrets User + Crypto User for both identities) |
| `storage` | `stdialed{env}{token}`: `photos` (CORS for the app origin) and `func-deploy` |
| `cosmos-account` / `cosmos-database` | shared free-tier account `cosmos-dialed-{token}`, database `dialed-{env}` (400 RU/s shared), container `identity`; data-plane RBAC |
| `registry` | `crdialed{env}{token}` (Basic, AcrPull for the API identity) |
| `containerapp` | `cae-dialed-{env}`, `ca-dialed-api-{env}` (Consumption, 0–2 replicas) |
| `functions` | `asp-dialed-func-{env}` (Flex Consumption), `func-dialed-{env}` (.NET 10 isolated) |
| `email` | Communication Services Email with an Azure-managed domain, sender role for both identities |

## Rules
- **No secrets in configuration.** App settings are endpoints and names. Cosmos has local (key) auth disabled, Storage has shared-key auth disabled, the container registry has no admin user. Services authenticate with their managed identity (`AZURE_CLIENT_ID` is set for `DefaultAzureCredential`).
- **Passkeys (dev):** the API's allowed origin list contains the Static Web App's default hostname (`Passkeys__Origins__0`, `App__Origins__0`); the RP ID is derived from the calling origin (ADR-0010). Prod sets a fixed `Passkeys__RpId` to the custom domain.
- **Cosmos free tier** exists once per subscription and only at creation. `dev` creates the account; for `prod` run `azd env set COSMOS_ACCOUNT_RESOURCE_GROUP rg-dialed-dev` so it only adds its own database to the shared account.
- **First deploy:** until `azd deploy` runs, the API container app runs a hello-world bootstrap image.
- **Data Protection keys** (ADR-0010) must be persisted to Storage/Key Vault before the API runs more than one replica; the Key Vault and roles are ready for that (#30).

CI compiles and lints the templates on every change (`infra` job).

## Deploying from GitHub (#25)
Every push to `main` runs `.github/workflows/deploy-dev.yml`: `azd provision` when `infra/**` or `azure.yaml` changed, then `azd deploy` for the API and Functions, then a smoke test of `/health` (API) and `/api/health` (Functions). It signs in with OIDC; GitHub holds no Azure secret.

One-time setup (needs `az login` and `gh auth login`, run from the repo root):
```
./infra/scripts/setup-github-oidc.ps1
```
It creates the app registration `dialed-github-deploy` with a federated credential for the GitHub environment `dev`, grants it Contributor and Role Based Access Control Administrator on the subscription (the Bicep creates role assignments), creates the `dev` environment and sets the variables `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_ENV_NAME` and `AZURE_LOCATION`. Until those exist the workflow skips itself. Run it manually from the Actions tab (with *provision* ticked) for the first deploy.

## Connecting GitHub to the PWA deploy (#22)
After `azd provision` (once per environment):
```
# deployment token of the Static Web App -> repository secret
gh secret set AZURE_STATIC_WEB_APPS_API_TOKEN --body "$(az staticwebapp secrets list --name swa-dialed-dev --resource-group rg-dialed-dev --query properties.apiKey -o tsv)"
# API address the dev and preview builds call -> repository variable
gh variable set DEV_API_URL --body "$(azd env get-value API_URL)"
```
Until both exist, `.github/workflows/web.yml` skips the deploy. PR previews are allowed by the API through the `App__Origins__1` pattern.
