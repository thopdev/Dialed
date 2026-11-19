// Dialed infrastructure for one environment (ADR-0007, architecture §7). Deploy with `azd provision`.
// Naming: {type}-dialed-{env}; names that must be globally unique get a short token.
targetScope = 'subscription'

@description('Environment name, for example dev or prod. Used in resource names.')
@minLength(2)
@maxLength(12)
param environmentName string

@description('Azure region for all resources.')
param location string = 'westeurope'

@description('Region for the Static Web App resource. It is only offered in a few regions (centralus, eastus2, westus2, westeurope, eastasia); the site itself is served from the global edge, so this only decides where its settings live. Empty means the same as location.')
param staticWebAppLocation string = ''

@description('Resource group of an existing shared Cosmos DB account. Empty (dev) creates the account in this environment; prod sets it to the dev resource group and only adds its own database.')
param cosmosAccountResourceGroup string = ''

@description('Use the free tier when the account is created ("true"/"false"; a string because azd fills it from the environment). One free-tier account per subscription.')
@allowed(['true', 'false'])
param cosmosFreeTier string = 'true'

@description('Resource id of an existing Container Apps environment for the API, in another resource group. A new subscription may have only one Container Apps environment in total, so prod runs in the dev one with its own app, identity and settings. Empty creates cae-dialed-{env}.')
param containerAppsEnvironmentId string = ''

@description('Region of the Container Apps environment and the API. A new subscription may have only one Container Apps environment per region, so prod uses another region than dev. Empty means the same as location.')
param apiLocation string = ''

@description('Region for the Cosmos DB account when it is created. Cosmos capacity for new subscriptions can run out in a region ("high demand"); empty means the same as location.')
param cosmosLocation string = ''

@description('Custom domain of the environment, for example dialed.app (no scheme, no app. prefix). Empty for dev, which uses Azure default hostnames. When set, the app origin is https://{appHostname} (default app.{domain}) and the passkey RP ID is fixed to the domain (ADR-0010). Prod refuses to deploy without it: a passkey registered on a temporary hostname would stop working once the domain is set.')
param customDomain string = ''

@description('Hostname the PWA is served from when customDomain is set, for example dialed.example.com. Empty means app.{customDomain}. Must be the domain itself or a subdomain of it, because the passkey RP ID is the domain.')
param appHostname string = ''

@description('Minimum replicas of the API container app ("0" scales to zero; a string because azd fills it from the environment). Dev 0, prod 1 to avoid cold starts.')
param apiMinReplicas string = '0'

@description('Claude model that reads bag labels, for example claude-haiku-5-5. Empty keeps the fake reader. Set it only after the key is in the Key Vault of this environment as secret ai-api-key (docs/ai-label-scan.md), or the API revision fails to start.')
param aiExtractionModel string = ''

@description('Base address of the Claude Messages API: Anthropic by default, or a Foundry resource endpoint.')
param aiEndpoint string = 'https://api.anthropic.com'

@description('API image set by azd after the first deploy; empty means the bootstrap image is used.')
param apiImageName string = ''

@description('Whether the API container app already exists ("true"/"false"; azd sets SERVICE_API_RESOURCE_EXISTS).')
@allowed(['true', 'false'])
param apiExists string = 'false'

var env = toLower(environmentName)
var token = take(uniqueString(subscription().id, env), 5)
var tags = {
  'azd-env-name': env
  app: 'dialed'
  environment: env
}

// Shared by all environments in the subscription, so its name does not contain the environment.
var cosmosAccountName = 'cosmos-dialed-${take(uniqueString(subscription().id), 6)}'
// Prod must have its domain before anything runs: changing the RP ID later invalidates every passkey (#111).
var domain = toLower(customDomain)
var hasCustomDomain = !empty(domain)
var requestedAppHost = toLower(appHostname)
// A passkey's RP ID must be the app's host or a parent of it, so any other host would make every sign-in fail.
var appHost = empty(requestedAppHost)
  ? 'app.${domain}'
  : (requestedAppHost == domain || endsWith(requestedAppHost, '.${domain}')
      ? requestedAppHost
      : fail('appHostname (APP_HOSTNAME) must be customDomain or a subdomain of it'))
// The check sits on the resource group, the first resource, so a prod deployment without a domain creates nothing.
var resourceGroupLocation = env == 'prod' && !hasCustomDomain ? fail('customDomain (CUSTOM_DOMAIN) is required for prod; see infra/README.md') : location

var createCosmosAccount = empty(cosmosAccountResourceGroup)
var cosmosResourceGroup = createCosmosAccount ? 'rg-dialed-${env}' : cosmosAccountResourceGroup

resource rg 'Microsoft.Resources/resourceGroups@2022-09-01' = {
  name: 'rg-dialed-${env}'
  location: resourceGroupLocation
  tags: tags
}

module monitoring 'modules/monitoring.bicep' = {
  scope: rg
  name: 'monitoring'
  params: {
    location: location
    tags: tags
    logAnalyticsName: 'log-dialed-${env}'
    appInsightsName: 'appi-dialed-${env}'
  }
}

module apiIdentity 'modules/identity.bicep' = {
  scope: rg
  name: 'identity-api'
  params: {
    location: location
    tags: tags
    name: 'id-dialed-api-${env}'
  }
}

module functionsIdentity 'modules/identity.bicep' = {
  scope: rg
  name: 'identity-functions'
  params: {
    location: location
    tags: tags
    name: 'id-dialed-func-${env}'
  }
}

module staticWebApp 'modules/staticwebapp.bicep' = {
  scope: rg
  name: 'static-web-app'
  params: {
    location: empty(staticWebAppLocation) ? location : staticWebAppLocation
    tags: tags
    name: 'swa-dialed-${env}'
  }
}

module keyVault 'modules/keyvault.bicep' = {
  scope: rg
  name: 'key-vault'
  params: {
    location: location
    tags: tags
    name: 'kv-dialed-${env}-${token}'
    secretsUserPrincipalIds: [apiIdentity.outputs.principalId, functionsIdentity.outputs.principalId]
  }
}

module storage 'modules/storage.bicep' = {
  scope: rg
  name: 'storage'
  params: {
    location: location
    tags: tags
    name: 'stdialed${env}${token}'
    appOrigins: [appOrigin]
    apiPrincipalId: apiIdentity.outputs.principalId
    functionsPrincipalId: functionsIdentity.outputs.principalId
  }
}

module cosmosAccount 'modules/cosmos-account.bicep' = if (createCosmosAccount) {
  scope: rg
  name: 'cosmos-account'
  params: {
    location: empty(cosmosLocation) ? location : cosmosLocation
    tags: tags
    name: cosmosAccountName
    enableFreeTier: cosmosFreeTier == 'true'
  }
}

module cosmosDatabase 'modules/cosmos-database.bicep' = {
  scope: resourceGroup(cosmosResourceGroup)
  // Per environment: dev and prod both deploy into the shared account's resource group, and two deployments with one
  // name collide there (DeploymentActive) when a dev and a prod deploy run at the same time.
  name: 'cosmos-database-${env}'
  dependsOn: [cosmosAccount]
  params: {
    name: cosmosAccountName
    databaseName: 'dialed-${env}'
    principalIds: [apiIdentity.outputs.principalId, functionsIdentity.outputs.principalId]
  }
}

module registry 'modules/registry.bicep' = {
  scope: rg
  name: 'registry'
  params: {
    location: location
    tags: tags
    name: 'crdialed${env}${token}'
    pullPrincipalId: apiIdentity.outputs.principalId
  }
}

module email 'modules/email.bicep' = {
  scope: rg
  name: 'email'
  params: {
    tags: tags
    emailServiceName: 'email-dialed-${env}'
    communicationServiceName: 'acs-dialed-${env}'
    senderPrincipalIds: [apiIdentity.outputs.principalId, functionsIdentity.outputs.principalId]
  }
}

// The origin the PWA is served from. With a custom domain only https://{appHost} is allowed (app.{domain} unless APP_HOSTNAME says otherwise), so the Static Web App's
// temporary hostname can never register a passkey; without one (dev) it is the default hostname plus PR previews.
var appOrigin = hasCustomDomain ? 'https://${appHost}' : staticWebApp.outputs.origin
var originSettings = hasCustomDomain
  ? {
      App__Origins__0: appOrigin
      Passkeys__Origins__0: appOrigin
      Passkeys__RpId: domain
    }
  : {
      App__Origins__0: appOrigin
      App__Origins__1: staticWebApp.outputs.previewOriginPattern
      Passkeys__Origins__0: appOrigin
    }

// Configuration is environment variables only: endpoints and names, never keys.
// Without a custom domain the passkey RP ID is not set: the API derives it from the calling origin against the allowlist (ADR-0010).
var sharedSettings = {
  APPLICATIONINSIGHTS_CONNECTION_STRING: monitoring.outputs.appInsightsConnectionString
  Cosmos__Endpoint: cosmosDatabase.outputs.endpoint
  Cosmos__DatabaseName: cosmosDatabase.outputs.databaseName
  Storage__BlobEndpoint: storage.outputs.blobEndpoint
  Storage__PhotosContainer: storage.outputs.photosContainer
  KeyVault__Uri: keyVault.outputs.uri
  Email__Endpoint: email.outputs.endpoint
  Email__SenderDomain: email.outputs.senderDomain
}

// The real label reader (#81): on only when a model is set; the key never leaves Key Vault (read with the API identity).
var hasAi = !empty(aiExtractionModel)
var aiSettings = hasAi
  ? {
      Ai__Endpoint: empty(aiEndpoint) ? 'https://api.anthropic.com' : aiEndpoint
      Ai__ExtractionModel: aiExtractionModel
    }
  : {}
var aiSecretSettings = hasAi ? { Ai__ApiKey: '${keyVault.outputs.uri}secrets/ai-api-key' } : {}

module apiImage 'modules/existing-image.bicep' = {
  scope: rg
  name: 'api-existing-image'
  params: {
    name: 'ca-dialed-api-${env}'
    exists: apiExists == 'true'
  }
}

module api 'modules/containerapp.bicep' = {
  scope: rg
  name: 'api'
  params: {
    location: empty(apiLocation) ? location : apiLocation
    tags: tags
    environmentName: 'cae-dialed-${env}'
    appName: 'ca-dialed-api-${env}'
    logAnalyticsId: monitoring.outputs.logAnalyticsId
    logAnalyticsCustomerId: monitoring.outputs.logAnalyticsCustomerId
    identityId: apiIdentity.outputs.id
    identityClientId: apiIdentity.outputs.clientId
    registryLoginServer: registry.outputs.loginServer
    imageName: apiImageName
    existingImage: apiImage.outputs.image
    minReplicas: int(empty(apiMinReplicas) ? '0' : apiMinReplicas)
    existingEnvironmentId: containerAppsEnvironmentId
    appSettings: union(sharedSettings, originSettings, aiSettings, {
      Jwt__KeyVaultKeyName: keyVault.outputs.jwtSigningKeyName
    })
    secretSettings: aiSecretSettings
  }
}

module functions 'modules/functions.bicep' = {
  scope: rg
  name: 'functions'
  params: {
    location: location
    tags: tags
    planName: 'asp-dialed-func-${env}'
    appName: 'func-dialed-${env}'
    identityId: functionsIdentity.outputs.id
    identityClientId: functionsIdentity.outputs.clientId
    storageAccountName: storage.outputs.name
    deploymentContainerUrl: storage.outputs.deploymentContainerUrl
    appSettings: sharedSettings
  }
}

// Outputs become azd environment values.
output AZURE_LOCATION string = location
output AZURE_RESOURCE_GROUP string = rg.name
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = registry.outputs.loginServer
output API_URL string = api.outputs.url
output WEB_URL string = staticWebApp.outputs.origin
output APP_ORIGIN string = appOrigin
output STATIC_WEB_APP_NAME string = staticWebApp.outputs.name
output FUNCTIONS_APP_NAME string = functions.outputs.name
output FUNCTIONS_URL string = 'https://${functions.outputs.hostname}'
output COSMOS_ENDPOINT string = cosmosDatabase.outputs.endpoint
output KEY_VAULT_URI string = keyVault.outputs.uri
