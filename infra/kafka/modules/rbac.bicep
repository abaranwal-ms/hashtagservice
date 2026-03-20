// ─────────────────────────────────────────────────────────────────────────────
// Module: rbac.bicep
//
// Grants the supplied principal two role assignments so that
// DefaultAzureCredential works without any connection strings:
//
//   1. Azure Event Hubs Data Owner   (namespace scope)
//      Covers all three services:
//        • PostGenerator    → send to posts-topic
//        • HashtagExtractor → receive from posts-topic + send to hashtags-topic
//        • HashtagPersister → receive from hashtags-topic
//
//   2. Storage Blob Data Contributor (storage account scope)
//      Covers both consumer services for EventProcessorClient checkpointing.
//
// Scope notes
// ───────────
// "Data Owner" at namespace scope is appropriate for a single-developer
// learning environment.  For production, prefer least-privilege per service:
//   PostGenerator:      "Azure Event Hubs Data Sender"   on posts-topic
//   HashtagExtractor:   "Azure Event Hubs Data Receiver" on posts-topic
//                       "Azure Event Hubs Data Sender"   on hashtags-topic
//   HashtagPersister:   "Azure Event Hubs Data Receiver" on hashtags-topic
// ─────────────────────────────────────────────────────────────────────────────

@description('Object (principal) ID of the user, service principal, or managed identity.')
param principalId string

@description('Principal type that matches principalId.')
@allowed(['User', 'ServicePrincipal', 'Group', 'ForeignGroup', 'Device'])
param principalType string = 'User'

@description('Resource ID of the Event Hubs namespace.')
param ehNamespaceId string

@description('Resource ID of the checkpoint storage account.')
param storageAccountId string

// ─── Built-in role definition IDs ────────────────────────────────────────────
// https://learn.microsoft.com/azure/role-based-access-control/built-in-roles
var eventHubsDataOwnerRoleId     = 'f526a384-b230-433a-b45c-95f59c4a2dec'
var storageBlobDataContribRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

// ─── Existing resource references (needed to use as role-assignment scope) ───
resource ehNamespace 'Microsoft.EventHub/namespaces@2021-11-01' existing = {
  name: last(split(ehNamespaceId, '/'))
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2022-09-01' existing = {
  name: last(split(storageAccountId, '/'))
}

// ─── 1. Azure Event Hubs Data Owner → namespace ───────────────────────────────
resource ehDataOwnerAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  // Deterministic GUID: changes only if the target resource, principal, or role changes
  name: guid(ehNamespaceId, principalId, eventHubsDataOwnerRoleId)
  scope: ehNamespace
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', eventHubsDataOwnerRoleId)
    principalId: principalId
    principalType: principalType
    description: 'HashtagService — Event Hubs Data Owner (send + receive on all topics)'
  }
}

// ─── 2. Storage Blob Data Contributor → checkpoint storage account ────────────
resource storageBlobContribAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccountId, principalId, storageBlobDataContribRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContribRoleId)
    principalId: principalId
    principalType: principalType
    description: 'HashtagService — Blob Data Contributor for EventProcessorClient checkpointing'
  }
}
