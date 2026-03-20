targetScope = 'resourceGroup'

@description('AAD object ID (managed identity or user) to assign Cosmos DB Built-in Data Contributor.')
param principalId string

// ── Cosmos DB Account ────────────────────────────────────────────────────────

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: 'hashtagservice-cosmos'
  location: 'centralindia'
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    locations: [
      {
        locationName: 'centralindia'
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
          { path: '/totalPostCount/?' }
          { path: '/isDeleted/?' }
        ]
        excludedPaths: [
          { path: '/*' }
        ]
      }
    }
  }
}

// ── Posts Container ───────────────────────────────────────────────────────────
// Partition key: /id (post GUID) — enables 1 RU point reads by post ID.
// Each post lands on its own logical partition for perfect write distribution.

resource postsContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: database
  name: 'Posts'
  properties: {
    resource: {
      id: 'Posts'
      partitionKey: {
        paths: [ '/id' ]
        kind: 'Hash'
      }
      indexingPolicy: {
        indexingMode: 'consistent'
        includedPaths: [
          { path: '/userId/?' }
          { path: '/createdAt/?' }
          { path: '/isDeleted/?' }
        ]
        excludedPaths: [
          { path: '/*' }
        ]
      }
    }
  }
}

// TODO: UserPartitions reverse-index container (partition key: /userId).
// One document per user tracking which YYYYMM buckets have post data.
// See Shared/Models/Post.cs for the design notes.

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
output hashtagsContainerName string = container.name
output postsContainerName string = postsContainer.name
