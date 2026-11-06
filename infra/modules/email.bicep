// Azure Communication Services Email with an Azure-managed sender domain (dev). Prod gets a custom domain in M4.
// Sign-in is by passkey; email is only for confirmation and recovery (ADR-0005).
param tags object
param emailServiceName string
param communicationServiceName string
param senderPrincipalIds array

// Built-in "Communication and Email Service Owner": lets an identity send mail through the Communication Service.
var emailSender = '09976791-48a7-449e-bb21-39d1a415f350'

// Communication Services are global; the data location decides where the data is stored.
var dataLocation = 'Europe'

resource emailService 'Microsoft.Communication/emailServices@2023-04-01' = {
  name: emailServiceName
  location: 'global'
  tags: tags
  properties: { dataLocation: dataLocation }
}

resource domain 'Microsoft.Communication/emailServices/domains@2023-04-01' = {
  parent: emailService
  name: 'AzureManagedDomain'
  location: 'global'
  tags: tags
  properties: { domainManagement: 'AzureManaged' }
}

resource communication 'Microsoft.Communication/communicationServices@2023-04-01' = {
  name: communicationServiceName
  location: 'global'
  tags: tags
  properties: {
    dataLocation: dataLocation
    linkedDomains: [domain.id]
  }
}

resource senders 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in senderPrincipalIds: {
  name: guid(communication.id, principalId, emailSender)
  scope: communication
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', emailSender)
  }
}]

output endpoint string = 'https://${communication.properties.hostName}'
output senderDomain string = domain.properties.mailFromSenderDomain
