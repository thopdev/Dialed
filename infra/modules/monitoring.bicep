// Log Analytics + Application Insights (workspace-based), shared by API, Functions and the PWA.
param location string
param tags object
param logAnalyticsName string
param appInsightsName string

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
    DisableLocalAuth: true
  }
}

output logAnalyticsId string = logs.id
output logAnalyticsCustomerId string = logs.properties.CustomerId
output appInsightsId string = insights.id
// A connection string is an endpoint + ingestion key id, not a secret (local auth is disabled; Entra ID is used to ingest).
output appInsightsConnectionString string = insights.properties.ConnectionString
