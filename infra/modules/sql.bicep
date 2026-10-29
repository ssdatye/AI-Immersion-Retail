// Azure SQL (Entra-only auth) with one serverless General Purpose database on the free offer:
// it auto-pauses when idle and pauses instead of billing when the monthly free limit is used.
// The developer is the Entra admin (loads the seed); the web identity gets read access to the
// seed schema from the seed command, not from here.
param location string
param nameSuffix string
param tags object
param adminLogin string
param adminObjectId string

@allowed([
  'User'
  'Group'
  'Application'
])
param adminPrincipalType string = 'User'

@description('Use the Azure SQL free offer (one per subscription region). Set false if the subscription has used it.')
param useFreeLimit bool = true

param databaseName string = 'costtoserve'

resource server 'Microsoft.Sql/servers@2023-08-01' = {
  name: 'cts-sql-${nameSuffix}'
  location: location
  tags: tags
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      login: adminLogin
      sid: adminObjectId
      tenantId: tenant().tenantId
      principalType: adminPrincipalType
    }
  }
}

// App Service has no fixed outbound address on the Basic tier without VNet integration, so
// Azure-internal traffic is allowed. Every connection still needs an Entra token (no SQL logins).
resource allowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01' = {
  parent: server
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: server
  name: databaseName
  location: location
  tags: tags
  sku: {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 1
  }
  properties: {
    autoPauseDelay: 60
    minCapacity: json('0.5')
    maxSizeBytes: 34359738368
    requestedBackupStorageRedundancy: 'Local'
    zoneRedundant: false
    useFreeLimit: useFreeLimit
    freeLimitExhaustionBehavior: useFreeLimit ? 'AutoPause' : null
  }
}

output serverName string = server.name
output serverFqdn string = server.properties.fullyQualifiedDomainName
output databaseName string = database.name
