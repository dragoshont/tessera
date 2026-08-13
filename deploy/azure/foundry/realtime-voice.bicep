targetScope = 'resourceGroup'

@description('Existing Azure AI Foundry account that owns the model deployment.')
param accountName string

@description('Deployment name consumed by Tessera Broker configuration.')
param deploymentName string = 'tessera-realtime-21'

@description('GlobalStandard capacity units allocated to the deployment.')
@minValue(1)
@maxValue(10)
param capacity int = 5

resource foundryAccount 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: accountName
}

resource realtimeDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: foundryAccount
  name: deploymentName
  sku: {
    name: 'GlobalStandard'
    capacity: capacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-realtime-2.1'
      version: '2026-07-07'
    }
    versionUpgradeOption: 'NoAutoUpgrade'
  }
}

output deploymentResourceId string = realtimeDeployment.id
output deploymentModel string = realtimeDeployment.properties.model.name
output deploymentModelVersion string = realtimeDeployment.properties.model.version