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

// The containers the API's Cosmos stores use. With data-plane RBAC the app can't create them, so they all live here.
// One entry per container, in alphabetical order by id, so PRs that each add a container touch different lines.
// defaultTtl -1: TTL on, every document sets its own `ttl`. See docs/cosmos-stores.md.
var containers = [
  // Identity (ADR-0010): users with passkeys, email and passkey lookups, profiles, refresh tokens, ceremony nonces and beta invites.
  // Partition key = id (`pk`); refresh tokens, nonces and invites expire through their `ttl` field.
  { id: 'identity', partitionKey: '/pk', defaultTtl: -1 }
  // Email recovery codes, send cooldown and lockout per account (#197, CosmosEmailRecoveryStore).
  { id: 'recovery', partitionKey: '/accountId', defaultTtl: -1 }
]

resource sqlContainers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = [for container in containers: {
  parent: database
  name: container.id
  properties: {
    resource: {
      id: container.id
      partitionKey: { paths: [container.partitionKey], kind: 'Hash' }
      defaultTtl: container.?defaultTtl
    }
  }
}]

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
