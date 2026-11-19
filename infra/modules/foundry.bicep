// Claude in Microsoft Foundry for the label reader (#79, #81): a Foundry (AI Services) account with one Claude deployment,
// reached by the API's managed identity (Cognitive Services User). Keys are off: Entra ID is the only way in.
// The first deployment accepts the Anthropic offer in Azure Marketplace (modelProviderData) and is billed per token.
// Shape follows Microsoft's starter kit (Azure-Samples/claude, infra-bicep/infra/foundry.bicep).
param location string
param tags object
param name string

@description('Claude model to deploy, for example claude-opus-5-5. The deployment has the same name, which the API sends as "model".')
param model string

@description('Model version: 2 is "Hosted on Azure" (data stays in Azure), 1 is "Hosted on Anthropic".')
param modelVersion string

@description('Capacity of the Global Standard deployment (thousands of tokens per minute).')
param capacity int

@description('Marketplace details Anthropic asks for: organization name, two-letter country code, industry.')
param organizationName string
param countryCode string
@allowed(['technology', 'finance', 'healthcare', 'education', 'retail', 'manufacturing', 'government', 'media', 'other'])
param industry string

@description('Principals that may call the model (the API identity).')
param userPrincipalIds array

resource account 'Microsoft.CognitiveServices/accounts@2025-10-01-preview' = {
  name: name
  location: location
  tags: tags
  kind: 'AIServices'
  sku: { name: 'S0' }
  identity: { type: 'SystemAssigned' }
  properties: {
    customSubDomainName: name
    allowProjectManagement: true
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
  }
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-10-01-preview' = {
  parent: account
  name: 'dialed'
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {}
}

resource deployment 'Microsoft.CognitiveServices/accounts/deployments@2025-10-01-preview' = {
  parent: account
  name: model
  dependsOn: [project]
  sku: { name: 'GlobalStandard', capacity: capacity }
  properties: {
    model: { format: 'Anthropic', name: model, version: modelVersion }
    // Not in the Bicep type definitions yet, but required for partner models: the resource provider accepts the offer with it.
    #disable-next-line BCP037
    modelProviderData: {
      organizationName: organizationName
      countryCode: countryCode
      industry: industry
    }
    versionUpgradeOption: 'OnceNewDefaultVersionAvailable'
    raiPolicyName: 'Microsoft.DefaultV2'
  }
}

// Cognitive Services User: call the deployed models with an Entra token (scope https://ai.azure.com/.default).
resource modelUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in userPrincipalIds: {
  name: guid(account.id, principalId, 'a97b65f3-24c7-4388-baec-2e87135dc908')
  scope: account
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}]

output endpoint string = 'https://${account.name}.services.ai.azure.com/anthropic'
output deploymentName string = deployment.name
