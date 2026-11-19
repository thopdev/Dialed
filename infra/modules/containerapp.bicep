// Container Apps environment + the API app (Consumption; scales to zero unless minReplicas is set). `azd deploy` replaces the bootstrap image.
// With existingEnvironmentId the app joins that environment instead of creating one (prod shares dev's, see infra/README.md).
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

@description('Image the app runs now (empty when it does not exist yet); used when imageName is empty so a provision keeps it.')
param existingImage string = ''

@description('Minimum replicas: 0 scales to zero (cold start of a few seconds), 1 keeps one replica warm.')
@minValue(0)
@maxValue(2)
param minReplicas int = 0

@description('Resource id of a Container Apps environment to run in instead of creating environmentName. The app must be in its region.')
param existingEnvironmentId string = ''

@description('Custom hostname of the API, for example api.example.com. Bound only together with customDomainCertificateId.')
param customDomainName string = ''

@description('Resource id of the managed certificate for customDomainName (created by the first `az containerapp hostname bind`). Every provision rewrites the ingress, so without it a bound hostname would be removed.')
param customDomainCertificateId string = ''

var createEnvironment = empty(existingEnvironmentId)
var customDomains = empty(customDomainName) || empty(customDomainCertificateId)
  ? []
  : [
      {
        name: customDomainName
        certificateId: customDomainCertificateId
        bindingType: 'SniEnabled'
      }
    ]

// Until the first `azd deploy` there is no API image, so the app starts from Microsoft's ASP.NET sample. It listens on
// 8080 like the API: `azd deploy` only swaps the image, so the ingress port must already be right.
var image = !empty(imageName) ? imageName : (!empty(existingImage) ? existingImage : 'mcr.microsoft.com/dotnet/samples:aspnetapp')

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: last(split(logAnalyticsId, '/'))
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = if (createEnvironment) {
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
    managedEnvironmentId: createEnvironment ? environment.id : existingEnvironmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        customDomains: customDomains
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
          image: image
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [for item in items(settings): { name: item.key, value: item.value }]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: 2
      }
    }
  }
}

output name string = app.name
output url string = 'https://${app.properties.configuration.ingress.fqdn}'
output environmentId string = createEnvironment ? environment.id : existingEnvironmentId
