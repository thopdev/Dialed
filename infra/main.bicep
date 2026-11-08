// Dialed infrastructure for one environment (ADR-0007, architecture §7). Deploy with `azd provision`.
// Naming: {type}-dialed-{env}; names that must be globally unique get a short token.
targetScope = 'subscription'

@description('Environment name, for example dev or prod. Used in resource names.')
@minLength(2)
@maxLength(12)
param environmentName string

@description('Azure region for all resources.')
param location string = 'westeurope'

@description('Resource group of an existing shared Cosmos DB account. Empty (dev) creates the account in this environment; prod sets it to the dev resource group and only adds its own database.')
param cosmosAccountResourceGroup string = ''

@description('Use the free tier when the account is created ("true"/"false"; a string because azd fills it from the environment). One free-tier account per subscription.')
@allowed(['true', 'false'])
param cosmosFreeTier string = 'true'

@description('API image set by azd after the first deploy; empty means the bootstrap image is used.')
param apiImageName string = ''

var env = toLower(environmentName)
var token = take(uniqueString(subscription().id, env), 5)
var tags = {
  'azd-env-name': env
  app: 'dialed'
  environment: env
}

// Shared by all environments in the subscription, so its name does not contain the environment.
var cosmosAccountName = 'cosmos-dialed-${take(uniqueString(subscription().id), 6)}'
var createCosmosAccount = empty(cosmosAccountResourceGroup)
var cosmosResourceGroup = createCosmosAccount ? 'rg-dialed-${env}' : cosmosAccountResourceGroup

resource rg 'Microsoft.Resources/resourceGroups@2022-09-01' = {
  name: 'rg-dialed-${env}'
  location: location
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
    location: location
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
    appOrigins: [staticWebApp.outputs.origin]
    apiPrincipalId: apiIdentity.outputs.principalId
    functionsPrincipalId: functionsIdentity.outputs.principalId
  }
}

module cosmosAccount 'modules/cosmos-account.bicep' = if (createCosmosAccount) {
  scope: rg
  name: 'cosmos-account'
  params: {
    location: location
    tags: tags
    name: cosmosAccountName
    enableFreeTier: cosmosFreeTier == 'true'
  }
}

module cosmosDatabase 'modules/cosmos-database.bicep' = {
  scope: resourceGroup(cosmosResourceGroup)
  name: 'cosmos-database'
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

// Configuration is environment variables only: endpoints and names, never keys.
// The passkey RP ID is not set for dev: the API derives it from the calling origin against the allowlist (ADR-0010).
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

module api 'modules/containerapp.bicep' = {
  scope: rg
  name: 'api'
  params: {
    location: location
    tags: tags
    environmentName: 'cae-dialed-${env}'
    appName: 'ca-dialed-api-${env}'
    logAnalyticsId: monitoring.outputs.logAnalyticsId
    logAnalyticsCustomerId: monitoring.outputs.logAnalyticsCustomerId
    identityId: apiIdentity.outputs.id
    identityClientId: apiIdentity.outputs.clientId
    registryLoginServer: registry.outputs.loginServer
    imageName: apiImageName
    appSettings: union(sharedSettings, {
      App__Origins__0: staticWebApp.outputs.origin
      App__Origins__1: staticWebApp.outputs.previewOriginPattern
      Passkeys__Origins__0: staticWebApp.outputs.origin
    })
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
output STATIC_WEB_APP_NAME string = staticWebApp.outputs.name
output FUNCTIONS_APP_NAME string = functions.outputs.name
output COSMOS_ENDPOINT string = cosmosDatabase.outputs.endpoint
output KEY_VAULT_URI string = keyVault.outputs.uri
