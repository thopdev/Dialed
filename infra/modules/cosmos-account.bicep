// Cosmos DB account (NoSQL). Free tier can only be chosen at creation and exists once per subscription,
// so the account is shared: `dev` and `prod` are two databases in it (architecture §7).
param location string
param tags object
param name string
param enableFreeTier bool

resource account 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' = {
  name: name
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    enableFreeTier: enableFreeTier
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    consistencyPolicy: { defaultConsistencyLevel: 'Session' }
    // Entra ID only: no account keys exist to leak.
    disableLocalAuth: true
    minimalTlsVersion: 'Tls12'
  }
}

output name string = account.name
