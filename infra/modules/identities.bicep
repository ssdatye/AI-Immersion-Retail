// User-assigned identities, created first so other resources can reference their IDs:
// - web: the studio's runtime identity (SQL, Cosmos, App Insights, AI gateway token).
// - deploy: GitHub Actions deploys through OIDC federation; no secret is stored anywhere.
param location string
param nameSuffix string
param tags object

@description('GitHub repository (owner/name) allowed to deploy. Empty skips the federated credential.')
param githubRepository string

@description('GitHub environment whose jobs may deploy (the OIDC subject is repo:<owner/name>:environment:<name>).')
param githubEnvironment string

resource web 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-cts-web-${nameSuffix}'
  location: location
  tags: tags
}

resource deploy 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-cts-deploy-${nameSuffix}'
  location: location
  tags: tags
}

resource github 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = if (!empty(githubRepository)) {
  parent: deploy
  name: 'github-${githubEnvironment}'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: 'repo:${githubRepository}:environment:${githubEnvironment}'
    audiences: [
      'api://AzureADTokenExchange'
    ]
  }
}

output webIdentityId string = web.id
output webIdentityName string = web.name
output webClientId string = web.properties.clientId
output webPrincipalId string = web.properties.principalId
output deployIdentityName string = deploy.name
output deployClientId string = deploy.properties.clientId
output deployPrincipalId string = deploy.properties.principalId
