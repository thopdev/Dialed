// Static Web App (Free) for the PWA. Content is deployed by the Static Web Apps GitHub Action (#22).
param tags object
param name string
// Static Web Apps are only available in a few regions; West Europe is one of them.
param location string

resource site 'Microsoft.Web/staticSites@2023-12-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {}
}

output name string = site.name
output defaultHostname string = site.properties.defaultHostname
output origin string = 'https://${site.properties.defaultHostname}'
