// Storage: `photos` (CORS for the app origin) and `func-deploy` (Flex Consumption deployment package).
param location string
param tags object
param name string
param appOrigins array
param apiPrincipalId string
param functionsPrincipalId string

var blobDataContributor = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var blobDelegator = 'db58b8e5-c6ad-4a2a-8342-4190687cbf4a'
var blobDataOwner = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
var queueDataContributor = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
var tableDataContributor = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'

resource account 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: name
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
  }
}

resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: account
  name: 'default'
  properties: {
    // The PWA uploads photos straight to Blob Storage with short-lived user delegation SAS (ADR-0008).
    cors: {
      corsRules: [
        {
          allowedOrigins: appOrigins
          allowedMethods: ['GET', 'PUT', 'HEAD', 'OPTIONS']
          allowedHeaders: ['*']
          exposedHeaders: ['ETag']
          maxAgeInSeconds: 3600
        }
      ]
    }
  }
}

resource photos 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobs
  name: 'photos'
  properties: { publicAccess: 'None' }
}

resource deployment 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobs
  name: 'func-deploy'
  properties: { publicAccess: 'None' }
}

var apiRoles = [blobDataContributor, blobDelegator]
var functionsRoles = [blobDataContributor, blobDelegator, blobDataOwner, queueDataContributor, tableDataContributor]

resource apiAssignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for role in apiRoles: {
  name: guid(account.id, apiPrincipalId, role)
  scope: account
  properties: {
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role)
  }
}]

resource functionsAssignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for role in functionsRoles: {
  name: guid(account.id, functionsPrincipalId, role)
  scope: account
  properties: {
    principalId: functionsPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role)
  }
}]

output name string = account.name
output blobEndpoint string = account.properties.primaryEndpoints.blob
output deploymentContainerUrl string = '${account.properties.primaryEndpoints.blob}${deployment.name}'
output photosContainer string = photos.name
