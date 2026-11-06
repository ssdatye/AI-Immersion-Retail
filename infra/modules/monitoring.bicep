// Log Analytics + workspace-based Application Insights. Local (key) auth is disabled:
// the studio and APIM send telemetry with managed identities (Monitoring Metrics Publisher).
param location string
param nameSuffix string
param tags object

@description('Principals that send telemetry (web identity).')
param publisherPrincipalIds array

var monitoringMetricsPublisherRoleId = '3913510d-42f4-4e42-8a64-420c390055eb'

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'cts-logs-${nameSuffix}'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
    workspaceCapping: {
      dailyQuotaGb: 1
    }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'cts-appi-${nameSuffix}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    DisableLocalAuth: true
    IngestionMode: 'LogAnalytics'
  }
}

resource publishers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in publisherPrincipalIds: {
  scope: appInsights
  name: guid(appInsights.id, principalId, monitoringMetricsPublisherRoleId)
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', monitoringMetricsPublisherRoleId)
  }
}]

output appInsightsId string = appInsights.id
output appInsightsName string = appInsights.name
// Not a credential: ingestion requires an Entra token because local auth is disabled.
output connectionString string = appInsights.properties.ConnectionString
output workspaceId string = workspace.id
