// Image the API container app runs now, so `azd provision` keeps it instead of resetting the app to the bootstrap image.
// CI starts from a fresh azd environment where SERVICE_API_IMAGE_NAME is empty; azd sets SERVICE_API_RESOURCE_EXISTS.
param name string
param exists bool

resource existingApp 'Microsoft.App/containerApps@2024-03-01' existing = if (exists) {
  name: name
}

output image string = exists ? existingApp!.properties.template.containers[0].image : ''
