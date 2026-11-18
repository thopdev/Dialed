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

// Friend codes, follows and blocks (#38). Partition key = `user:{id}` or `code:{CODE}` (CosmosFriendStore); nothing expires.
resource friends 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: database
  name: 'friends'
  properties: {
    resource: {
      id: 'friends'
      partitionKey: { paths: ['/pk'], kind: 'Hash' }
    }
  }
}

// Households and their invites (#38), one partition per household; invites expire through their `ttl` field.
resource households 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: database
  name: 'households'
  properties: {
    resource: {
      id: 'households'
      partitionKey: { paths: ['/householdId'], kind: 'Hash' }
      defaultTtl: -1
    }
  }
}

// Users, passkeys and lookup documents (ADR-0010). Partition key = id; documents expire through the `ttl` field (ceremony state).
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

// Public profiles and reports (#38, #106), one partition per person; nothing expires.
resource publicprofiles 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: database
  name: 'publicprofiles'
  properties: {
    resource: {
      id: 'publicprofiles'
      partitionKey: { paths: ['/userId'], kind: 'Hash' }
    }
  }
}

// Synced records (#38, ADR-0011), one partition per household. The app's JSON under /entity is not indexed (queries only filter on the
// copied top-level fields), so a write only pays for indexing those few fields. Soft-deleted records expire through their `ttl` field.
resource sync 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: database
  name: 'sync'
  properties: {
    resource: {
      id: 'sync'
      partitionKey: { paths: ['/householdId'], kind: 'Hash' }
      defaultTtl: -1
      indexingPolicy: {
        indexingMode: 'consistent'
        automatic: true
        includedPaths: [{ path: '/*' }]
        excludedPaths: [{ path: '/entity/*' }, { path: '/"_etag"/?' }]
      }
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
