targetScope = 'resourceGroup'

@description('AAD object ID (managed identity or user) to assign Cosmos DB Built-in Data Contributor.')
param principalId string

// ── Cosmos DB Account ────────────────────────────────────────────────────────

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: 'hashtagservice-cosmos'
  location: 'southindia'
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    locations: [
      {
        locationName: 'southindia'
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    capabilities: [
      { name: 'EnableServerless' }
    ]
    disableLocalAuth: false
  }
}

// ── SQL Database ─────────────────────────────────────────────────────────────

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  parent: cosmosAccount
  name: 'HashtagServiceDb'
  properties: {
    resource: {
      id: 'HashtagServiceDb'
    }
  }
}

// ── Hashtags Container ───────────────────────────────────────────────────────

resource container 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: database
  name: 'Hashtags'
  properties: {
    resource: {
      id: 'Hashtags'
      partitionKey: {
        paths: [ '/hashtag' ]
        kind: 'Hash'
      }
      indexingPolicy: {
        indexingMode: 'consistent'
        includedPaths: [
          { path: '/hashtag/?' }
          { path: '/postId/?' }
          { path: '/extractedAt/?' }
        ]
        excludedPaths: [
          { path: '/*' }
        ]
      }
    }
  }
}

// ── RBAC: Cosmos DB Built-in Data Contributor ────────────────────────────────
// Role definition ID 00000000-0000-0000-0000-000000000002 is the built-in
// "Cosmos DB Built-in Data Contributor" role (read + write, no management plane).

resource roleAssignment 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, principalId, '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: principalId
    scope: cosmosAccount.id
  }
}

// ── Outputs ──────────────────────────────────────────────────────────────────

output cosmosAccountName string = cosmosAccount.name
output cosmosEndpoint string = cosmosAccount.properties.documentEndpoint
output databaseName string = database.name
output containerName string = container.name
