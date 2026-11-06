// Container Apps environment + the API app (Consumption, scale to zero in dev). `azd deploy` replaces the bootstrap image.
param location string
param tags object
param environmentName string
param appName string
param logAnalyticsCustomerId string
param logAnalyticsId string
param identityId string
param identityClientId string
param registryLoginServer string
param imageName string
param appSettings object

// Until the first `azd deploy` there is no API image, so the app starts from Microsoft's hello-world image (port 80).
var bootstrap = empty(imageName)

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: last(split(logAnalyticsId, '/'))
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsCustomerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

var settings = union({
  AZURE_CLIENT_ID: identityClientId
  ASPNETCORE_FORWARDEDHEADERS_ENABLED: 'true'
}, appSettings)

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: appName
  location: location
  tags: union(tags, { 'azd-service-name': 'api' })
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identityId}': {} }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: bootstrap ? 80 : 8080
        transport: 'auto'
        allowInsecure: false
      }
      registries: [
        {
          server: registryLoginServer
          identity: identityId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: bootstrap ? 'mcr.microsoft.com/k8se/quickstart:latest' : imageName
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [for item in items(settings): { name: item.key, value: item.value }]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 2
      }
    }
  }
}

output name string = app.name
output url string = 'https://${app.properties.configuration.ingress.fqdn}'
output environmentId string = environment.id
