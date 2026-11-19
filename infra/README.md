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
| `containerapp` | `cae-dialed-{env}`, `ca-dialed-api-{env}` (Consumption, `API_MIN_REPLICAS` to 2 replicas) |
| `functions` | `asp-dialed-func-{env}` (Flex Consumption), `func-dialed-{env}` (.NET 10 isolated) |
| `email` | Communication Services Email with an Azure-managed domain, sender role for both identities |

## Regions
`AZURE_LOCATION` (default `westeurope`) sets the region for everything except the Static Web App. New subscriptions can be refused in busy regions (`RequestDisallowedByAzure`, "not accepting new customers"); pick another region with `azd env set AZURE_LOCATION <region>` locally, or the `AZURE_LOCATION` variable in GitHub. The Static Web App is only offered in `centralus`, `eastus2`, `westus2`, `westeurope` and `eastasia`; set `STATIC_WEB_APP_LOCATION` when `AZURE_LOCATION` is not one of them. Its region only decides where the resource's settings live: the site is served from the global edge. Cosmos DB can be short of capacity for new subscriptions (`ServiceUnavailable`, "high demand"); `COSMOS_LOCATION` puts only the Cosmos account in another region (dev uses `swedencentral` next to `northeurope`). A failed attempt can leave the account in a Failed state; delete it before retrying.

## Rules
- **No secrets in configuration.** App settings are endpoints and names. Cosmos has local (key) auth disabled, Storage has shared-key auth disabled, the container registry has no admin user. Services authenticate with their managed identity (`AZURE_CLIENT_ID` is set for `DefaultAzureCredential`).
- **Passkeys (dev):** the API's allowed origin list contains the Static Web App's default hostname (`Passkeys__Origins__0`, `App__Origins__0`); the RP ID is derived from the calling origin (ADR-0010).
- **Passkeys (prod):** with `CUSTOM_DOMAIN` set, the only allowed origin is `https://app.<domain>` (or `https://<APP_HOSTNAME>` when that is set; it must be the domain or a subdomain of it) and `Passkeys__RpId` is `<domain>`; the Static Web App's own hostname and PR previews are not allowed. `infra/main.bicep` fails for `prod` without a domain, so no passkey is ever registered on a temporary hostname (changing the RP ID later invalidates every passkey).
- **Cosmos free tier** exists once per subscription and only at creation. `dev` creates the account; for `prod` run `azd env set COSMOS_ACCOUNT_RESOURCE_GROUP rg-dialed-dev` so it only adds its own database to the shared account.
- **First deploy:** until `azd deploy` runs, the API container app runs a hello-world bootstrap image.
- **JWT signing** (#30): the RSA key `jwt-signing` in Key Vault signs the API's access tokens (RS256); the API signs through Key Vault with its managed identity (Key Vault Crypto User) and validates with the public keys of every enabled version. Rotate by adding a version (`az keyvault key rotate --vault-name <vault> --name jwt-signing`): new tokens use it within an hour, old ones stay valid until they expire. Disable an old version only after the access-token lifetime (30 minutes) has passed.
- **API image on provision:** CI starts from a fresh azd environment, so `infra/main.bicep` reads the image the API already runs (`SERVICE_API_RESOURCE_EXISTS`) instead of resetting it. A brand-new app starts from the ASP.NET sample image on port 8080, the same port as the API, because `azd deploy` only swaps the image.
- **Data Protection keys** (ADR-0010) must be persisted to Storage/Key Vault before the API runs more than one replica; the Key Vault and roles are ready for that (#30).

CI compiles and lints the templates on every change (`infra` job).

## Deploying from GitHub (#25)
Every push to `main` runs `.github/workflows/deploy-dev.yml`, which calls the shared steps in `.github/workflows/deploy-azure.yml`: `azd provision` when `infra/**` or `azure.yaml` changed, then `azd deploy` for the API and Functions, then a smoke test of `/health` (API) and `/api/health` (Functions). It signs in with OIDC; GitHub holds no Azure secret.

One-time setup (needs `az login` and `gh auth login`, run from the repo root):
```
./infra/scripts/setup-github-oidc.ps1
```
It creates the app registration `dialed-github-deploy` with a federated credential for the GitHub environment `dev`, grants it Contributor and Role Based Access Control Administrator on the subscription (the Bicep creates role assignments), creates the `dev` environment and sets the repository variables `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_ENV_NAME`, `AZURE_LOCATION`, `STATIC_WEB_APP_LOCATION` and `API_MIN_REPLICAS` (and `COSMOS_LOCATION` with `-CosmosLocation swedencentral`). Until those exist the workflow skips itself. Run it manually from the Actions tab (with *provision* ticked) for the first deploy.

## Connecting GitHub to the PWA deploy (#22)
After `azd provision` (once per environment):
```
# deployment token of the Static Web App -> repository secret
gh secret set AZURE_STATIC_WEB_APPS_API_TOKEN --body "$(az staticwebapp secrets list --name swa-dialed-dev --resource-group rg-dialed-dev --query properties.apiKey -o tsv)"
# API address the dev and preview builds call -> repository variable
gh variable set DEV_API_URL --body "$(azd env get-value API_URL)"
```
Until both exist, `.github/workflows/web.yml` skips the deploy. PR previews are allowed by the API through the `App__Origins__1` pattern.

## Production (#28)
`prod` is a second azd environment next to `dev`: its own resource group `rg-dialed-prod` (Static Web App, API, Functions, Key Vault, Storage, registry, email) in the same regions as dev (North Europe, Static Web App in East US 2), and its own database `dialed-prod` in dev's shared free-tier Cosmos account (Sweden Central).

### How a release reaches prod
1. Tag a commit on `main` and push the tag: `git tag v1.0.0`, then `git push origin v1.0.0` (or run *Deploy prod* by hand from the Actions tab).
2. `.github/workflows/deploy-prod.yml` waits for a required reviewer to approve the `prod` environment.
3. It runs the shared steps (`deploy-azure.yml`) with the `prod` environment's variables: `azd provision` (always), `azd deploy` for the API and Functions, then it publishes the PWA to `swa-dialed-prod`. The Static Web App deployment token is read with the same OIDC sign-in, so prod has no stored secret.
4. Smoke test: API `/health` must answer `Healthy`; Functions `/api/health` and the PWA must answer 200.

The workflow stops at its first step when `CUSTOM_DOMAIN` is empty, or when `AZURE_ENV_NAME` is not `prod` (the environment's variables are missing and the repository's dev values would be used).

### One-time setup
1. Register the domain (#111).
2. From the repo root (needs `az login` and `gh auth login`):
   ```
   ./infra/scripts/setup-github-oidc.ps1 -Environment prod -CustomDomain <domain>
   ```
   It reuses the app registration `dialed-github-deploy` and its subscription roles, adds the federated credential `github-prod` (subject `<prefix>:environment:prod`, prefix read from GitHub like dev), creates the GitHub environment `prod` if it is missing (an existing one keeps its protection rules) and sets these **environment** variables, which override the repository ones that dev uses:

   | Variable | prod value |
   |---|---|
   | `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | same app registration as dev |
   | `AZURE_ENV_NAME` | `prod` |
   | `AZURE_LOCATION` | `northeurope` |
   | `STATIC_WEB_APP_LOCATION` | `eastus2` |
   | `COSMOS_ACCOUNT_RESOURCE_GROUP` | `rg-dialed-dev` (adds `dialed-prod` to the shared account) |
   | `API_MIN_REPLICAS` | `1` (see below) |
   | `CUSTOM_DOMAIN` | `<domain>`, only when `-CustomDomain` is given; without it the deploy refuses to run |
   | `APP_HOSTNAME` | only with `-AppHostname`: the PWA's hostname when it is not `app.<domain>`, for example the domain itself |
   | `API_PUBLIC_URL` | not set by the script: set it to `https://api.<domain>` once that hostname is bound; until then the PWA calls the Container App's own address |

   Without `-CustomDomain` the environment is prepared but cannot deploy; add the domain later with `gh variable set CUSTOM_DOMAIN --env prod --body <domain>`.
3. **Required reviewers** (owner setting, not scripted): GitHub → repository **Settings** → **Environments** → **prod** → tick **Required reviewers**, add yourself, untick **Allow administrators to bypass configured protection rules** if the approval should apply to you too, and click **Save protection rules**. Under **Deployment branches and tags** choose **Selected branches and tags** and add a tag rule `v*` (and the branch `main` if manual runs from `main` should be allowed), so only releases can deploy.
4. Run *Deploy prod* once (tag or manual run), add the DNS records below, bind the hostnames, set `API_PUBLIC_URL` and run it again.

### API minimum replicas
`API_MIN_REPLICAS` sets the Container App's minimum replicas (the maximum stays 2). Dev uses `0`: it scales to zero, and the first request after a quiet period waits for a cold start (several seconds for the .NET API). The proposed prod default is `1`, so a beta tester never waits for that.

Cost of `1` at Consumption list prices (North Europe, 0.5 vCPU and 1 GiB per replica): idle time is billed at $0.000003 per vCPU-second and $0.000003 per GiB-second, so one always-on replica costs about **$11.70 a month** when idle (0.5 × 0.000003 + 1 × 0.000003 = $0.0000045 per second × 2.59 million seconds). Seconds spent serving requests cost more ($0.000024 per vCPU-second), and the monthly free grant (180,000 vCPU-seconds and 360,000 GiB-seconds per subscription, shared with dev) takes off about $1.40. With `0` the API costs next to nothing between requests. Change it with `gh variable set API_MIN_REPLICAS --env prod --body 0`; the next deploy applies it.

The Data Protection keys must be persisted before the API runs more than one replica (see *Rules*); until then a second replica can break a passkey ceremony that started on the other one.

### Cosmos DB backup policy (owner choice)
Backup is a setting of the Cosmos **account**, so it applies to `dialed-dev` and `dialed-prod` together. The account uses the default **periodic** policy now: a backup every 4 hours, 8 hours kept, geo-redundant.

| Option | What you get | Cost |
|---|---|---|
| Periodic (now) | 2 backups, every 4 hours; restore only through an Azure support request, into a new account | 2 copies free; longer retention $0.247 per GB a month (geo-redundant) |
| **Continuous, 7 days** (proposed) | Restore to any second of the last 7 days yourself (portal or CLI), per database or container, into a new account | Backup storage free; a restore costs $0.15 per GB restored |
| Continuous, 30 days | As above, 30 days back | $0.20 per GB a month, plus $0.15 per GB restored |

Prices are Sweden Central list prices. With a few hundred MB of beta data every option costs cents. Moving from periodic to continuous **cannot be undone**. To switch to the proposed option once chosen:
```
az cosmosdb update --name cosmos-dialed-4wfy3v --resource-group rg-dialed-dev --backup-policy-type Continuous --continuous-tier Continuous7Days
```
Then add the same `backupPolicy` to `infra/modules/cosmos-account.bicep` so a recreated account matches.

### DNS records for the domain (#111)
Values in angle brackets come from the first prod provision (`azd env get-values` or the portal). The managed certificates are free; if the domain has CAA records, allow `digicert.com`.

| Name | Type | Value | What for |
|---|---|---|---|
| `app` (or the `APP_HOSTNAME` name) | CNAME | default hostname of `swa-dialed-prod` (`<name>.azurestaticapps.net`) | The PWA. Then `az staticwebapp hostname set --name swa-dialed-prod --resource-group rg-dialed-prod --hostname <app host>` validates through the CNAME and issues the certificate. On Cloudflare set the record to **DNS only** (grey cloud), or validation and the certificate fail. |
| `api` | CNAME | FQDN of `ca-dialed-api-prod` (`<name>.northeurope.azurecontainerapps.io`) | The API. |
| `asuid.api` | TXT | `customDomainVerificationId` from `az containerapp show -n ca-dialed-api-prod -g rg-dialed-prod --query properties.customDomainVerificationId -o tsv` | Proves the domain to Container Apps. Then `az containerapp hostname add` and `az containerapp hostname bind --hostname api.<domain> --environment cae-dialed-prod --validation-method CNAME` (managed certificate). |
| `@` | TXT | `ms-domain-verification=<value>` | Verifies the email domain for Azure Communication Services (shown when the custom email domain is added). |
| `@` | TXT | `v=spf1 include:spf.protection.outlook.com -all` | SPF for ACS email; merge it with an existing SPF record, a domain can have only one. |
| `selector1-azurecomm-prod-net._domainkey` | CNAME | `selector1-azurecomm-prod-net._domainkey.azurecomm.net` | DKIM key 1 (ACS shows the exact values). |
| `selector2-azurecomm-prod-net._domainkey` | CNAME | `selector2-azurecomm-prod-net._domainkey.azurecomm.net` | DKIM key 2. |
| `_dmarc` | TXT | `v=DMARC1; p=none; rua=mailto:<address>` | Optional, recommended so mail providers trust the sender. |

The email module still uses an Azure-managed sending domain; connecting the custom one is part of #111 and #199.
