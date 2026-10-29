// App Service plan (Linux B2) and the studio web app (.NET 10, Always On). The app serves the
// front end and API, runs as the user-assigned web identity, and accepts traffic only from the
// APIM gateway. App settings hold endpoints and IDs only; there are no secrets.
param location string
param tags object
param webAppName string
param planName string
param webIdentityId string
param webClientId string

@description('APIM public IP; the only address allowed to reach the app.')
param apimPublicIp string

param sqlServerFqdn string
param sqlDatabaseName string
param cosmosEndpoint string
param cosmosDatabaseName string
param appInsightsConnectionString string

@description('AI gateway endpoint the agent calls, for example https://<apim>.azure-api.net/ai/.')
param modelEndpoint string
param modelDeployment string

@description('Entra app registration (client) ID for studio sign-in. Empty disables sign-in (the app then rejects all API calls).')
param entraClientId string

@description('RegionalOps users\' regions, keyed by Entra object ID (for example Luis -> Southeast).')
param regionalOpsRegions object

param deployPrincipalId string

var websiteContributorRoleId = 'de139f84-1756-47ae-9be6-808fbbe84772'

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: 'B2'
    tier: 'Basic'
  }
  properties: {
    reserved: true
  }
}

var baseSettings = [
  {
    name: 'ASPNETCORE_ENVIRONMENT'
    value: 'Production'
  }
  {
    name: 'AZURE_CLIENT_ID'
    value: webClientId
  }
  {
    name: 'SCM_DO_BUILD_DURING_DEPLOYMENT'
    value: 'false'
  }
  {
    name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
    value: appInsightsConnectionString
  }
  {
    name: 'CostToServe__DataMode'
    value: 'Azure'
  }
  {
    name: 'CostToServe__ActionConfigFile'
    value: 'config/action-levers.csv'
  }
  {
    name: 'CostToServe__Sql__ConnectionString'
    value: 'Server=tcp:${sqlServerFqdn},1433;Database=${sqlDatabaseName};Encrypt=True;TrustServerCertificate=False;Connect Timeout=60'
  }
  {
    name: 'CostToServe__Cosmos__Endpoint'
    value: cosmosEndpoint
  }
  {
    name: 'CostToServe__Cosmos__Database'
    value: cosmosDatabaseName
  }
  {
    name: 'Foundry__Endpoint'
    value: modelEndpoint
  }
  {
    name: 'Foundry__Deployment'
    value: modelDeployment
  }
  {
    name: 'Entra__TenantId'
    value: empty(entraClientId) ? '' : tenant().tenantId
  }
  {
    name: 'Entra__ClientId'
    value: entraClientId
  }
]

// App Service on Linux rejects hyphens in setting names, so the object-ID map is one value.
var regionSettings = empty(regionalOpsRegions) ? [] : [
  {
    name: 'Entra__RegionalOpsAssignments'
    value: join(map(items(regionalOpsRegions), item => '${item.key}=${item.value}'), ';')
  }
]

resource web 'Microsoft.Web/sites@2024-04-01' = {
  name: webAppName
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${webIdentityId}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    keyVaultReferenceIdentity: webIdentityId
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      appCommandLine: 'dotnet CostToServe.Api.dll'
      alwaysOn: true
      http20Enabled: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      healthCheckPath: '/api/health'
      appSettings: concat(baseSettings, regionSettings)
      ipSecurityRestrictionsDefaultAction: 'Deny'
      ipSecurityRestrictions: [
        {
          name: 'apim-gateway'
          description: 'Only the APIM front gate may call the studio.'
          ipAddress: '${apimPublicIp}/32'
          action: 'Allow'
          priority: 100
        }
      ]
      // Deployments use the SCM site with Entra tokens (basic auth is disabled below).
      scmIpSecurityRestrictionsUseMain: false
      scmIpSecurityRestrictionsDefaultAction: 'Allow'
    }
  }
}

resource ftpCredentials 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: web
  name: 'ftp'
  properties: {
    allow: false
  }
}

resource scmCredentials 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: web
  name: 'scm'
  properties: {
    allow: false
  }
}

// GitHub Actions (OIDC) may deploy code to this one app and nothing else.
resource deployAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: web
  name: guid(web.id, deployPrincipalId, websiteContributorRoleId)
  properties: {
    principalId: deployPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', websiteContributorRoleId)
  }
}

output name string = web.name
output defaultHostName string = web.properties.defaultHostName
