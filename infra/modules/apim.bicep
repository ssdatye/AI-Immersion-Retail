// One API Management Developer instance hosting both gateways (ADR 000, layout option 2):
// - studio API (empty URL suffix): front gate for the What-If Studio, backend = App Service.
// - ai API (/ai): AI gateway, backend = the Foundry account's OpenAI v1 endpoint.
// APIM's system-assigned identity calls Foundry and sends telemetry to Application Insights.
param location string
param nameSuffix string
param tags object
param publisherEmail string
param publisherName string = 'Cost-to-Serve demo (illustrative)'

@description('Default host name of the studio web app (backend of the front gate).')
param studioHostName string

@description('Foundry OpenAI v1 endpoint, for example https://<account>.openai.azure.com/openai/v1/.')
param foundryOpenAIEndpoint string
param foundryAccountName string

@description('Client ID of the studio identity, the only caller allowed through the AI gateway.')
param studioClientId string

param appInsightsId string

@description('Application Insights connection string (not a credential: local auth is disabled).')
param appInsightsConnectionString string

var openAIUserRoleId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
var monitoringMetricsPublisherRoleId = '3913510d-42f4-4e42-8a64-420c390055eb'

resource apim 'Microsoft.ApiManagement/service@2024-05-01' = {
  name: 'cts-apim-${nameSuffix}'
  location: location
  tags: tags
  sku: {
    name: 'Developer'
    capacity: 1
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    publisherEmail: publisherEmail
    publisherName: publisherName
  }
}

resource foundry 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: foundryAccountName
}

resource foundryAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: foundry
  name: guid(foundry.id, apim.id, openAIUserRoleId)
  properties: {
    principalId: apim.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', openAIUserRoleId)
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: last(split(appInsightsId, '/'))
}

resource telemetryAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: appInsights
  name: guid(appInsights.id, apim.id, monitoringMetricsPublisherRoleId)
  properties: {
    principalId: apim.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', monitoringMetricsPublisherRoleId)
  }
}

resource logger 'Microsoft.ApiManagement/service/loggers@2024-05-01' = {
  parent: apim
  name: 'appinsights'
  properties: {
    loggerType: 'applicationInsights'
    description: 'Application Insights with APIM managed identity'
    resourceId: appInsightsId
    credentials: {
      connectionString: appInsightsConnectionString
      identityClientId: 'SystemAssigned'
    }
  }
  dependsOn: [
    telemetryAccess
  ]
}

// Traces with W3C correlation so APIM spans join the studio's traces. No bodies or headers are logged.
resource diagnostics 'Microsoft.ApiManagement/service/diagnostics@2024-05-01' = {
  parent: apim
  name: 'applicationinsights'
  properties: {
    loggerId: logger.id
    alwaysLog: 'allErrors'
    httpCorrelationProtocol: 'W3C'
    verbosity: 'information'
    logClientIp: false
    sampling: {
      samplingType: 'fixed'
      percentage: 100
    }
    frontend: {
      request: {
        body: {
          bytes: 0
        }
      }
      response: {
        body: {
          bytes: 0
        }
      }
    }
    backend: {
      request: {
        body: {
          bytes: 0
        }
      }
      response: {
        body: {
          bytes: 0
        }
      }
    }
  }
}

resource tenantIdValue 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  parent: apim
  name: 'tenant-id'
  properties: {
    displayName: 'tenant-id'
    value: tenant().tenantId
  }
}

resource studioClientIdValue 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  parent: apim
  name: 'studio-client-id'
  properties: {
    displayName: 'studio-client-id'
    value: studioClientId
  }
}

var wildcardOperations = [
  {
    name: 'get-root'
    method: 'GET'
    urlTemplate: '/'
  }
  {
    name: 'get-any'
    method: 'GET'
    urlTemplate: '/*'
  }
  {
    name: 'post-any'
    method: 'POST'
    urlTemplate: '/*'
  }
]

resource studioApi 'Microsoft.ApiManagement/service/apis@2024-05-01' = {
  parent: apim
  name: 'studio'
  properties: {
    displayName: 'What-If Studio (front gate)'
    path: ''
    protocols: [
      'https'
    ]
    serviceUrl: 'https://${studioHostName}'
    subscriptionRequired: false
  }
}

resource studioOperations 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = [for op in wildcardOperations: {
  parent: studioApi
  name: op.name
  properties: {
    displayName: '${op.method} ${op.urlTemplate}'
    method: op.method
    urlTemplate: op.urlTemplate
  }
}]

resource studioPolicy 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = {
  parent: studioApi
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: loadTextContent('../apim/studio-policy.xml')
  }
}

resource aiApi 'Microsoft.ApiManagement/service/apis@2024-05-01' = {
  parent: apim
  name: 'ai-gateway'
  properties: {
    displayName: 'AI gateway (Foundry OpenAI v1)'
    path: 'ai'
    protocols: [
      'https'
    ]
    serviceUrl: endsWith(foundryOpenAIEndpoint, '/') ? take(foundryOpenAIEndpoint, max(length(foundryOpenAIEndpoint) - 1, 0)) : foundryOpenAIEndpoint
    subscriptionRequired: false
  }
}

resource aiOperations 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = [for op in skip(wildcardOperations, 1): {
  parent: aiApi
  name: op.name
  properties: {
    displayName: '${op.method} ${op.urlTemplate}'
    method: op.method
    urlTemplate: op.urlTemplate
  }
}]

resource aiPolicy 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = {
  parent: aiApi
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: loadTextContent('../apim/ai-gateway-policy.xml')
  }
  dependsOn: [
    tenantIdValue
    studioClientIdValue
  ]
}

output name string = apim.name
output gatewayUrl string = apim.properties.gatewayUrl
output publicIp string = apim.properties.publicIPAddresses[0]
output principalId string = apim.identity.principalId
