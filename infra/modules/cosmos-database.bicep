// A per-environment database (shared throughput) in the shared Cosmos account, plus data-plane RBAC for the app identities.
param name string
param databaseName string
param principalIds array

// Built-in "Cosmos DB Built-in Data Contributor" (data plane, not the Azure RBAC role of the same kind).
var dataContributorRoleId = '00000000-0000-0000-0000-000000000002'

resource account 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' existing = {
  name: name
}

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' = {
  parent: account
  name: databaseName
  properties: {
    resource: { id: databaseName }
    // 400 RU/s shared by all containers; free tier covers 1000 RU/s per account.
    options: { throughput: 400 }
  }
}

// AI calls per user, operation and UTC day (#88). Each counter expires through its `ttl` field two days later.
resource aiquota 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: database
  name: 'aiquota'
  properties: {
    resource: {
      id: 'aiquota'
      partitionKey: { paths: ['/userId'], kind: 'Hash' }
      defaultTtl: -1
    }
  }
}

// The shared bean and roaster catalog (#268): the latest build in parts and the owner's rules. No user data; documents have no `ttl`.
resource catalog 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: database
  name: 'catalog'
  properties: {
    resource: {
      id: 'catalog'
      partitionKey: { paths: ['/pk'], kind: 'Hash' }
      defaultTtl: -1
    }
  }
}

// Identity (ADR-0010, docs/cosmos-stores.md): users with passkeys, email and passkey lookups, profiles, refresh tokens, ceremony
// nonces and beta invites. Partition key = id (`pk`); refresh tokens, nonces and invites expire through their `ttl` field.
resource identity 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: database
  name: 'identity'
  properties: {
    resource: {
      id: 'identity'
      partitionKey: { paths: ['/pk'], kind: 'Hash' }
      defaultTtl: -1
    }
  }
}

// Push devices, switches, stock alert memory and one-push-per-event markers (#260). The markers expire through their `ttl` field.
resource push 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: database
  name: 'push'
  properties: {
    resource: {
      id: 'push'
      partitionKey: { paths: ['/userId'], kind: 'Hash' }
      defaultTtl: -1
    }
  }
}

resource assignments 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = [for principalId in principalIds: {
  parent: account
  name: guid(account.id, database.name, principalId, dataContributorRoleId)
  properties: {
    roleDefinitionId: '${account.id}/sqlRoleDefinitions/${dataContributorRoleId}'
    principalId: principalId
    scope: '${account.id}/dbs/${database.name}'
  }
}]

output databaseName string = database.name
output endpoint string = account.properties.documentEndpoint
