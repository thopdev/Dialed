// Key Vault for secrets that cannot be avoided (JWT signing key, Data Protection key ring wrapping key). RBAC only, no access policies.
param location string
param tags object
param name string
param secretsUserPrincipalIds array

var keyVaultSecretsUser = '4633458b-17de-40ea-8c3d-17a9d1ca38fb'
var keyVaultCryptoUser = '12338af0-0e69-4776-bea7-57ae8d297424'

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
  }
}

resource secretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in secretsUserPrincipalIds: {
  name: guid(vault.id, principalId, keyVaultSecretsUser)
  scope: vault
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser)
  }
}]

// Data Protection keys are wrapped with a Key Vault key (ADR-0010), so the identities need to wrap/unwrap.
resource cryptoUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in secretsUserPrincipalIds: {
  name: guid(vault.id, principalId, keyVaultCryptoUser)
  scope: vault
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultCryptoUser)
  }
}]

output name string = vault.name
output uri string = vault.properties.vaultUri
