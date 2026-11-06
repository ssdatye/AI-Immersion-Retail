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

@description('Create the isolated Foundry project used only for evaluations (no connections).')
param deployEvalsProject bool = true

var accountName = 'cts-foundry-${nameSuffix}'

// Built-in role: Cognitive Services OpenAI User (inference only, no management rights).
var openAIUserRoleId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
// Built-in roles for running evaluations in the project: Foundry User (formerly Azure AI User) and
// Cognitive Services User (data plane).
var azureAIUserRoleId = '53ca6127-db72-4b80-b1b0-d745d6d5456d'
var cognitiveServicesUserRoleId = 'a97b65f3-24c7-4388-baec-2e87135dc908'

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

// Isolated evaluation project: holds evaluation runs only. It has no connections (no App Insights,
// no storage), so evaluated content never mixes with telemetry. The judge uses the account's model.
resource evalsProject 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = if (deployEvalsProject) {
  parent: account
  name: 'proj-evals'
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    displayName: 'Cost-to-Serve evaluations'
    description: 'Evaluation runs for the Cost-to-Serve demo agent (illustrative data).'
  }
  // The account accepts one write at a time.
  dependsOn: [
    deployment
  ]
}

resource developerEvalRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for roleId in [azureAIUserRoleId, cognitiveServicesUserRoleId]: if (deployEvalsProject && !empty(developerPrincipalId)) {
  scope: account
  name: guid(account.id, developerPrincipalId, roleId)
  properties: {
    principalId: developerPrincipalId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
  }
}]

output accountName string = account.name
output evalsProjectEndpoint string = deployEvalsProject ? 'https://${account.properties.customSubDomainName}.services.ai.azure.com/api/projects/proj-evals' : ''
output openAIEndpoint string = 'https://${account.properties.customSubDomainName}.openai.azure.com/openai/v1/'
output deploymentName string = deployment.name
output evalsProjectId string = deployEvalsProject ? '${account.id}/projects/proj-evals' : ''
