<#
.SYNOPSIS
  One-time setup so GitHub Actions can deploy Dialed to Azure without any stored secret (ADR-0007, #25, #28).

.DESCRIPTION
  Creates an Entra app registration with a federated credential for the GitHub environment, gives it the
  rights `azd provision` needs on the subscription, creates the GitHub environment and sets the variables the
  deploy workflows read. Run it once per environment; safe to run again (an existing GitHub environment, and
  the required reviewers set on it, are left as they are).

  dev keeps its variables on the repository (deploy-dev.yml). Every other environment (prod) gets them on the
  GitHub environment itself, where they override the repository ones, so the two never mix.

  Needs: Azure CLI (`az login` done, rights to create app registrations and role assignments on the
  subscription) and GitHub CLI (`gh auth login` done, admin rights on the repository).

.EXAMPLE
  ./infra/scripts/setup-github-oidc.ps1
  ./infra/scripts/setup-github-oidc.ps1 -Environment prod
  ./infra/scripts/setup-github-oidc.ps1 -Environment prod -CustomDomain example.com
  ./infra/scripts/setup-github-oidc.ps1 -Environment prod -CustomDomain dialed.example.com -AppHostname dialed.example.com
#>
[CmdletBinding()]
param(
    [string]$Repository = 'thopdev/Dialed',
    [string]$Environment = 'dev',
    [string]$Location = 'northeurope',
    # The Static Web App is only offered in a few regions (centralus, eastus2, westus2, westeurope, eastasia).
    [string]$StaticWebAppLocation = 'eastus2',
    # Region of the shared Cosmos account when dev creates it; prod reuses dev's account (CosmosAccountResourceGroup).
    [string]$CosmosLocation = '',
    # Resource group of the shared free-tier Cosmos account. Defaults to rg-dialed-dev for every environment but dev.
    [string]$CosmosAccountResourceGroup = '',
    # Minimum API replicas. Defaults to 0 for dev (scale to zero) and 1 for other environments (no cold start).
    [string]$ApiMinReplicas = '',
    # Domain such as example.com (app.<domain>, api.<domain>, passkey RP ID). Prod refuses to deploy without it.
    [string]$CustomDomain = '',
    # Hostname of the PWA when it is not app.<domain>, for example the domain itself. Must be the domain or a subdomain of it.
    [string]$AppHostname = '',
    [string]$AppName = 'dialed-github-deploy'
)

$ErrorActionPreference = 'Stop'

function Invoke-Native {
    # Stops the script when a native command fails (PowerShell only does that for its own errors).
    param([scriptblock]$Command)
    $output = & $Command
    if ($LASTEXITCODE -ne 0) { throw "Command failed: $Command" }
    return $output
}

$account = Invoke-Native { az account show --output json } | ConvertFrom-Json
$subscriptionId = $account.id
$tenantId = $account.tenantId
Write-Host "Subscription: $($account.name) ($subscriptionId)"
Write-Host "Tenant:       $tenantId"

# 1. App registration + service principal ------------------------------------------------------------
$clientId = Invoke-Native { az ad app list --display-name $AppName --query '[0].appId' --output tsv }
if (-not $clientId) {
    $clientId = Invoke-Native { az ad app create --display-name $AppName --query appId --output tsv }
    Write-Host "Created app registration $AppName ($clientId)"
}
$servicePrincipalId = Invoke-Native { az ad sp list --filter "appId eq '$clientId'" --query '[0].id' --output tsv }
if (-not $servicePrincipalId) {
    $servicePrincipalId = Invoke-Native { az ad sp create --id $clientId --query id --output tsv }
    Write-Host 'Created service principal'
}

# 2. Federated credential: only workflow runs in the GitHub environment can sign in ----------------------
# GitHub may send an ID-based subject (repo:owner@id/name@id:...) instead of repo:owner/name:...; ask it which prefix it uses.
$subjectPrefix = "repo:${Repository}"
$customization = Invoke-Native { gh api "repos/$Repository/actions/oidc/customization/sub" } | ConvertFrom-Json
if ($customization.sub_claim_prefix) { $subjectPrefix = $customization.sub_claim_prefix }
$subject = "${subjectPrefix}:environment:${Environment}"
$credentialName = "github-$Environment"
$existing = Invoke-Native { az ad app federated-credential list --id $clientId --query "[?subject=='$subject'].name" --output tsv }
if (-not $existing) {
    $taken = Invoke-Native { az ad app federated-credential list --id $clientId --query "[?name=='$credentialName'].name" --output tsv }
    if ($taken) { $credentialName = "$credentialName-ids" }
    $credential = @{
        name      = $credentialName
        issuer    = 'https://token.actions.githubusercontent.com'
        subject   = $subject
        audiences = @('api://AzureADTokenExchange')
    } | ConvertTo-Json
    $file = New-TemporaryFile
    Set-Content -Path $file -Value $credential
    try { Invoke-Native { az ad app federated-credential create --id $clientId --parameters "@$file" --output none } }
    finally { Remove-Item $file }
    Write-Host "Created federated credential for $subject"
}

# 3. Rights on the subscription: create resources, and the role assignments the Bicep makes ---------------
$scope = "/subscriptions/$subscriptionId"
foreach ($role in 'Contributor', 'Role Based Access Control Administrator') {
    $assigned = Invoke-Native { az role assignment list --assignee $servicePrincipalId --role $role --scope $scope --query '[0].id' --output tsv }
    if (-not $assigned) {
        Invoke-Native { az role assignment create --assignee-object-id $servicePrincipalId --assignee-principal-type ServicePrincipal --role $role --scope $scope --output none }
        Write-Host "Assigned $role"
    }
}

# 4. GitHub: environment + variables (identifiers only, there are no secrets) -----------------------------
# Create the environment only when it is missing: a PUT on an existing one could reset the owner's protection rules.
$environments = Invoke-Native { gh api "repos/$Repository/environments" --jq '.environments[].name' }
if (@($environments) -notcontains $Environment) {
    Invoke-Native { gh api --method PUT "repos/$Repository/environments/$Environment" --silent }
    Write-Host "Created GitHub environment $Environment"
}
$isDev = $Environment -eq 'dev'
if (-not $CosmosAccountResourceGroup -and -not $isDev) { $CosmosAccountResourceGroup = 'rg-dialed-dev' }
if (-not $ApiMinReplicas) { $ApiMinReplicas = if ($isDev) { '0' } else { '1' } }

$variables = [ordered]@{
    AZURE_CLIENT_ID               = $clientId
    AZURE_TENANT_ID               = $tenantId
    AZURE_SUBSCRIPTION_ID         = $subscriptionId
    AZURE_ENV_NAME                = $Environment
    AZURE_LOCATION                = $Location
    STATIC_WEB_APP_LOCATION       = $StaticWebAppLocation
    COSMOS_LOCATION               = $CosmosLocation
    COSMOS_ACCOUNT_RESOURCE_GROUP = $CosmosAccountResourceGroup
    API_MIN_REPLICAS              = $ApiMinReplicas
    CUSTOM_DOMAIN                 = $CustomDomain
    APP_HOSTNAME                  = $AppHostname
}
# dev reads repository variables (as before); other environments get their own, which override those.
$target = if ($isDev) { @() } else { @('--env', $Environment) }
foreach ($name in $variables.Keys) {
    $value = $variables[$name]
    if (-not $value) { continue }
    Invoke-Native { gh variable set $name --repo $Repository @target --body $value }
    Write-Host "Set $name = $value$(if (-not $isDev) { " (environment $Environment)" })"
}

Write-Host ''
if ($isDev) {
    Write-Host "Done. Pushes to main now deploy to 'dev' (.github/workflows/deploy-dev.yml)."
} else {
    Write-Host "Done. Release tags (v*) now deploy to '$Environment' (.github/workflows/deploy-prod.yml)."
    if (-not $CustomDomain) { Write-Host 'CUSTOM_DOMAIN is not set: the deploy refuses to run until it is (infra/README.md).' }
    Write-Host 'Add required reviewers to the environment in the repository settings (infra/README.md).'
}
