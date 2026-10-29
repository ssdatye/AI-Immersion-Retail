// Foundry (AIServices) account with one model deployment.
// Local key auth is disabled: callers use Entra ID (developer sign-in locally,
// managed identity on Azure). The default content filter policy is applied.
param location string
param nameSuffix string
param modelName string
param modelVersion string
param modelCapacity int
param developerPrincipalId string
param tags object

var accountName = 'cts-foundry-${nameSuffix}'

// Built-in role: Cognitive Services OpenAI User (inference only, no management rights).
var openAIUserRoleId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'

resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: accountName
  location: location
  tags: tags
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    customSubDomainName: accountName
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
    allowProjectManagement: true
  }
}

resource deployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: account
  name: modelName
  sku: {
    name: 'GlobalStandard'
    capacity: modelCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: modelName
      version: modelVersion
    }
    versionUpgradeOption: 'NoAutoUpgrade'
    raiPolicyName: 'Microsoft.DefaultV2'
  }
}

resource developerModelAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(developerPrincipalId)) {
  scope: account
  name: guid(account.id, developerPrincipalId, openAIUserRoleId)
  properties: {
    principalId: developerPrincipalId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', openAIUserRoleId)
  }
}

output accountName string = account.name
output openAIEndpoint string = 'https://${account.properties.customSubDomainName}.openai.azure.com/openai/v1/'
output deploymentName string = deployment.name
