// ─────────────────────────────────────────────────────────────────────────────
// Module: storage.bicep
//
// Provisions:
//   • Azure Blob Storage account  (Standard LRS, StorageV2)
//   • extractor-checkpoints       – EventProcessorClient checkpoints for
//                                   HashtagExtractor (HashTagCounter)
//   • persister-checkpoints       – EventProcessorClient checkpoints for
//                                   HashtagPersister
//
// Each consumer service gets its own container so checkpoint offsets are
// fully isolated (Rule: never share checkpoint containers between consumers).
// ─────────────────────────────────────────────────────────────────────────────

@description('Azure region.')
param location string

@description('Storage account name (max 24 chars, lowercase alphanumeric, globally unique).')
param storageAccountName string

@description('Resource tags applied to all resources in this module.')
param tags object = {}

// ─── Storage Account ─────────────────────────────────────────────────────────
resource storageAccount 'Microsoft.Storage/storageAccounts@2022-09-01' = {
  name: storageAccountName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'   // locally-redundant – cost-optimal for a learning environment
  }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false    // containers are private; access via RBAC only
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

// ─── Blob Service ─────────────────────────────────────────────────────────────
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2022-09-01' = {
  parent: storageAccount
  name: 'default'
}

// ─── extractor-checkpoints ───────────────────────────────────────────────────
// Used by HashtagExtractor/HashTagCounter when consuming posts-topic.
// BlobContainerClient URI format:
//   https://<account>.blob.core.windows.net/extractor-checkpoints
resource extractorContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2022-09-01' = {
  parent: blobService
  name: 'extractor-checkpoints'
  properties: {
    publicAccess: 'None'
  }
}

// ─── persister-checkpoints ────────────────────────────────────────────────────
// Used by HashtagPersister when consuming hashtags-topic.
// BlobContainerClient URI format:
//   https://<account>.blob.core.windows.net/persister-checkpoints
resource persisterContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2022-09-01' = {
  parent: blobService
  name: 'persister-checkpoints'
  properties: {
    publicAccess: 'None'
  }
}

// ─── Outputs ─────────────────────────────────────────────────────────────────
@description('Resource ID of the storage account.')
output storageAccountId string = storageAccount.id

@description('Full Blob URI for the extractor checkpoint container (CheckpointStorage:BlobUri in HashtagExtractor).')
output extractorCheckpointBlobUri string = '${storageAccount.properties.primaryEndpoints.blob}${extractorContainer.name}'

@description('Full Blob URI for the persister checkpoint container (CheckpointStorage:BlobUri in HashtagPersister).')
output persisterCheckpointBlobUri string = '${storageAccount.properties.primaryEndpoints.blob}${persisterContainer.name}'
