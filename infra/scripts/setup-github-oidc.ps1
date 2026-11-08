<#
.SYNOPSIS
  One-time setup so GitHub Actions can deploy Dialed to Azure without any stored secret (ADR-0007, #25).

.DESCRIPTION
  Creates an Entra app registration with a federated credential for the GitHub environment, gives it the
  rights `azd provision` needs on the subscription, creates the GitHub environment and sets the repository
  variables the deploy workflow reads. Safe to run again.

  Needs: Azure CLI (`az login` done, rights to create app registrations and role assignments on the
  subscription) and GitHub CLI (`gh auth login` done, admin rights on the repository).

.EXAMPLE
  ./infra/scripts/setup-github-oidc.ps1
  ./infra/scripts/setup-github-oidc.ps1 -Environment dev -Location westeurope
#>
[CmdletBinding()]
param(
    [string]$Repository = 'thopdev/Dialed',
    [string]$Environment = 'dev',
    [string]$Location = 'westeurope',
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
$credentialName = "github-$Environment"
$existing = Invoke-Native { az ad app federated-credential list --id $clientId --query "[?name=='$credentialName'].name" --output tsv }
if (-not $existing) {
    $credential = @{
        name      = $credentialName
        issuer    = 'https://token.actions.githubusercontent.com'
        subject   = "repo:${Repository}:environment:${Environment}"
        audiences = @('api://AzureADTokenExchange')
    } | ConvertTo-Json
    $file = New-TemporaryFile
    Set-Content -Path $file -Value $credential
    try { Invoke-Native { az ad app federated-credential create --id $clientId --parameters "@$file" --output none } }
    finally { Remove-Item $file }
    Write-Host "Created federated credential for repo:${Repository}:environment:${Environment}"
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
Invoke-Native { gh api --method PUT "repos/$Repository/environments/$Environment" --silent }
$variables = @{
    AZURE_CLIENT_ID       = $clientId
    AZURE_TENANT_ID       = $tenantId
    AZURE_SUBSCRIPTION_ID = $subscriptionId
    AZURE_ENV_NAME        = $Environment
    AZURE_LOCATION        = $Location
}
foreach ($name in $variables.Keys) {
    Invoke-Native { gh variable set $name --repo $Repository --body $variables[$name] }
}

Write-Host ''
Write-Host "Done. Pushes to main now deploy to '$Environment' (.github/workflows/deploy-dev.yml)."
