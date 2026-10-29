// Cosmos DB for NoSQL, serverless, Entra-only (local keys disabled).
// decisions: decision log (partition /actionId). threads: chat transcripts (/ownerId).
// scenarios: saved scenarios and evaluation runs (/ownerId).
param location string
param nameSuffix string
param tags object
param databaseName string = 'costtoserve'

@description('Principals given the built-in Data Contributor role (web identity, developer for reset).')
param dataContributorPrincipalIds array

var dataContributorRoleId = '00000000-0000-0000-0000-000000000002'

resource account 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' = {
  name: 'cts-cosmos-${nameSuffix}'
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    capabilities: [
      {
        name: 'EnableServerless'
      }
    ]
    disableLocalAuth: true
    minimalTlsVersion: 'Tls12'
    publicNetworkAccess: 'Enabled'
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
  }
}

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' = {
  parent: account
  name: databaseName
  properties: {
    resource: {
      id: databaseName
    }
  }
}

var containers = [
  {
    name: 'decisions'
    partitionKey: '/actionId'
  }
  {
    name: 'threads'
    partitionKey: '/ownerId'
  }
  {
    name: 'scenarios'
    partitionKey: '/ownerId'
  }
]

resource container 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = [for c in containers: {
  parent: database
  name: c.name
  properties: {
    resource: {
      id: c.name
      partitionKey: {
        paths: [
          c.partitionKey
        ]
        kind: 'Hash'
      }
    }
  }
}]

resource contributors 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = [for principalId in dataContributorPrincipalIds: {
  parent: account
  name: guid(account.id, principalId, dataContributorRoleId)
  properties: {
    principalId: principalId
    roleDefinitionId: '${account.id}/sqlRoleDefinitions/${dataContributorRoleId}'
    scope: account.id
  }
}]

output endpoint string = account.properties.documentEndpoint
output accountName string = account.name
output databaseName string = database.name
