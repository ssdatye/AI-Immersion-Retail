// Cost-to-Serve demo: subscription-scope entry point.
// Creates ONE new, dedicated resource group and deploys everything into it.
// It never reads or modifies any other resource group.
targetScope = 'subscription'

@description('Azure region for the demo resource group and resources.')
param location string = 'eastus2'

@description('Name of the new, dedicated demo resource group.')
param resourceGroupName string = 'rg-cost-to-serve-demo'

@description('Model deployed to Foundry for the agent.')
@allowed([
  'gpt-4.1-mini'
  'gpt-4.1'
])
param modelName string = 'gpt-4.1-mini'

param modelVersion string = '2025-04-14'

@description('GlobalStandard capacity in thousands of tokens per minute.')
@minValue(1)
@maxValue(200)
param modelCapacity int = 30

@description('Object ID of the developer who runs the agent locally (granted model access on the demo Foundry account only). Leave empty to skip.')
param developerPrincipalId string = ''

@description('Deploy the hosted platform (Increment 5): APIM, App Service, SQL, Cosmos, Key Vault, monitoring, deploy identity, budget. False deploys only Foundry (Increment 2).')
param deployPlatform bool = false

@description('Developer sign-in name, shown as the Azure SQL Entra admin.')
param developerLogin string = ''

@description('Email for the APIM publisher and budget alerts. Passed at deployment time; never committed.')
param contactEmail string = ''

@description('Entra app registration (client) ID for studio sign-in.')
param entraClientId string = ''

@description('RegionalOps users\' regions keyed by Entra object ID. Passed at deployment time from local settings.')
param regionalOpsRegions object = {}

@description('GitHub repository allowed to deploy through OIDC. Empty skips the federated credential.')
param githubRepository string = 'ssdatye/AI-Immersion-Retail'

param githubEnvironment string = 'demo'

@description('Region for the Azure SQL server only. East US 2 was not accepting new SQL servers (RegionDoesNotAllowProvisioning) on October 29, 2026.')
param sqlLocation string = 'centralus'

@description('Region for the App Service plan and web app. The subscription had no App Service quota in East US 2 on October 29, 2026 (SubscriptionIsOverQuotaForSku, limit 0).')
param appLocation string = 'centralus'

@description('Use the Azure SQL free offer. Set false if this subscription already used it.')
param useSqlFreeLimit bool = true

@description('Monthly budget alert amount in USD for the demo resource group.')
param budgetAmount int = 100

param budgetStartDate string = '2026-10-01'

param tags object = {
  demo: 'walmart-cost-to-serve'
  environment: 'demo'
  managedBy: 'bicep'
  data: 'illustrative'
}

var nameSuffix = uniqueString(subscription().id, resourceGroupName)
var webAppName = 'cts-web-${nameSuffix}'

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

module foundry 'modules/foundry.bicep' = {
  scope: rg
  name: 'foundry'
  params: {
    location: location
    nameSuffix: nameSuffix
    modelName: modelName
    modelVersion: modelVersion
    modelCapacity: modelCapacity
    developerPrincipalId: developerPrincipalId
    tags: tags
  }
}

module identities 'modules/identities.bicep' = if (deployPlatform) {
  scope: rg
  name: 'identities'
  params: {
    location: location
    nameSuffix: nameSuffix
    tags: tags
    githubRepository: githubRepository
    githubEnvironment: githubEnvironment
  }
}

module monitoring 'modules/monitoring.bicep' = if (deployPlatform) {
  scope: rg
  name: 'monitoring'
  params: {
    location: location
    nameSuffix: nameSuffix
    tags: tags
    publisherPrincipalIds: [
      identities!.outputs.webPrincipalId
    ]
  }
}

module keyVault 'modules/keyvault.bicep' = if (deployPlatform) {
  scope: rg
  name: 'keyvault'
  params: {
    location: location
    nameSuffix: nameSuffix
    tags: tags
    readerPrincipalId: identities!.outputs.webPrincipalId
  }
}

module cosmos 'modules/cosmos.bicep' = if (deployPlatform) {
  scope: rg
  name: 'cosmos'
  params: {
    location: location
    nameSuffix: nameSuffix
    tags: tags
    dataContributorPrincipalIds: concat([identities!.outputs.webPrincipalId], empty(developerPrincipalId) ? [] : [developerPrincipalId])
  }
}

module sql 'modules/sql.bicep' = if (deployPlatform) {
  scope: rg
  name: 'sql'
  params: {
    location: sqlLocation
    nameSuffix: nameSuffix
    tags: tags
    adminLogin: developerLogin
    adminObjectId: developerPrincipalId
    useFreeLimit: useSqlFreeLimit
  }
}

module apim 'modules/apim.bicep' = if (deployPlatform) {
  scope: rg
  name: 'apim'
  params: {
    location: location
    nameSuffix: nameSuffix
    tags: tags
    publisherEmail: contactEmail
    studioHostName: '${webAppName}.azurewebsites.net'
    foundryOpenAIEndpoint: foundry.outputs.openAIEndpoint
    foundryAccountName: foundry.outputs.accountName
    studioClientId: identities!.outputs.webClientId
    appInsightsId: monitoring!.outputs.appInsightsId
    appInsightsConnectionString: monitoring!.outputs.connectionString
  }
}

module web 'modules/appservice.bicep' = if (deployPlatform) {
  scope: rg
  name: 'appservice'
  params: {
    location: appLocation
    tags: tags
    webAppName: webAppName
    planName: 'cts-plan-${nameSuffix}'
    webIdentityId: identities!.outputs.webIdentityId
    webClientId: identities!.outputs.webClientId
    apimPublicIp: apim!.outputs.publicIp
    sqlServerFqdn: sql!.outputs.serverFqdn
    sqlDatabaseName: sql!.outputs.databaseName
    cosmosEndpoint: cosmos!.outputs.endpoint
    cosmosDatabaseName: cosmos!.outputs.databaseName
    appInsightsConnectionString: monitoring!.outputs.connectionString
    modelEndpoint: '${apim!.outputs.gatewayUrl}/ai/'
    modelDeployment: foundry.outputs.deploymentName
    entraClientId: entraClientId
    regionalOpsRegions: regionalOpsRegions
    deployPrincipalId: identities!.outputs.deployPrincipalId
  }
}

module budget 'modules/budget.bicep' = if (deployPlatform && !empty(contactEmail)) {
  scope: rg
  name: 'budget'
  params: {
    amount: budgetAmount
    contactEmails: [
      contactEmail
    ]
    startDate: budgetStartDate
  }
}

output resourceGroupName string = rg.name
output foundryAccountName string = foundry.outputs.accountName
output foundryOpenAIEndpoint string = foundry.outputs.openAIEndpoint
output modelDeploymentName string = foundry.outputs.deploymentName
output studioUrl string = deployPlatform ? '${apim!.outputs.gatewayUrl}/' : ''
output webAppName string = deployPlatform ? web!.outputs.name : ''
output apimName string = deployPlatform ? apim!.outputs.name : ''
output sqlServerName string = deployPlatform ? sql!.outputs.serverName : ''
output sqlServerFqdn string = deployPlatform ? sql!.outputs.serverFqdn : ''
output sqlDatabaseName string = deployPlatform ? sql!.outputs.databaseName : ''
output cosmosEndpoint string = deployPlatform ? cosmos!.outputs.endpoint : ''
output cosmosDatabaseName string = deployPlatform ? cosmos!.outputs.databaseName : ''
output webIdentityName string = deployPlatform ? identities!.outputs.webIdentityName : ''
output deployIdentityClientId string = deployPlatform ? identities!.outputs.deployClientId : ''
output keyVaultName string = deployPlatform ? keyVault!.outputs.name : ''
output appInsightsName string = deployPlatform ? monitoring!.outputs.appInsightsName : ''
