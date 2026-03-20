// ─────────────────────────────────────────────────────────────────────────────
// main.bicep — HashtagService Event Hubs Infrastructure
//
// Target scope : resourceGroup
// Subscription : 67e53100-61d9-49b5-8176-ad06015325bf
// Resource group: hashtagservice  (region: southindia)
//
// Resources provisioned
// ─────────────────────
//   Module eventhubs  → Event Hubs namespace  hashtagservice-eh
//                        Event Hub            posts-topic     (3 partitions)
//                        Event Hub            hashtags-topic  (3 partitions)
//                        Consumer group       $Default on each topic
//
//   Module storage    → Storage account       hashtsvcchk<8-char-unique-suffix>
//                        Blob container       extractor-checkpoints
//                        Blob container       persister-checkpoints
//
//   Module rbac       → Role: Azure Event Hubs Data Owner        → EH namespace
//                        Role: Storage Blob Data Contributor      → storage account
//
// Deploy via:  cd infra && ./deploy.sh
// ─────────────────────────────────────────────────────────────────────────────

targetScope = 'resourceGroup'

// ─── Parameters ──────────────────────────────────────────────────────────────

@description('Azure region for all resources.')
param location string = 'southindia'

@description('Event Hubs namespace name.  Must be globally unique within Azure.')
param ehNamespaceName string = 'hashtagservice-eh'

@description('''
Object (principal) ID of the identity to receive RBAC role assignments.
Obtain with: az ad signed-in-user show --query id -o tsv
This parameter is intentionally omitted from dev.bicepparam and is always
injected at deploy time by deploy.sh to avoid committing user-specific IDs.
''')
param principalId string

@description('Principal type matching principalId.')
@allowed(['User', 'ServicePrincipal', 'Group', 'ForeignGroup', 'Device'])
param principalType string = 'User'

@description('Short label applied to every resource as an "environment" tag.')
param environmentTag string = 'dev'

// ─── Variables ───────────────────────────────────────────────────────────────

// Storage account names must be ≤ 24 chars, lowercase alphanumeric, globally unique.
// We derive an 8-char suffix from the resource group ID so re-deployments in the
// same resource group always resolve to the same account name.
var storageAccountName = 'hashtsvcchk${take(uniqueString(resourceGroup().id), 8)}'

var commonTags = {
  project    : 'HashtagService'
  environment: environmentTag
  managedBy  : 'bicep'
}

// ─── Module: Event Hubs ───────────────────────────────────────────────────────
module eventhubs 'modules/eventhubs.bicep' = {
  name: 'deploy-eventhubs'
  params: {
    location     : location
    namespaceName: ehNamespaceName
    tags         : commonTags
  }
}

// ─── Module: Checkpoint Blob Storage ─────────────────────────────────────────
module storage 'modules/storage.bicep' = {
  name: 'deploy-storage'
  params: {
    location          : location
    storageAccountName: storageAccountName
    tags              : commonTags
  }
}

// ─── Module: RBAC role assignments ───────────────────────────────────────────
module rbac 'modules/rbac.bicep' = {
  name: 'deploy-rbac'
  params: {
    principalId    : principalId
    principalType  : principalType
    ehNamespaceId  : eventhubs.outputs.namespaceId
    storageAccountId: storage.outputs.storageAccountId
  }
}

// ─── Outputs (consumed by deploy.sh to patch appsettings.json files) ─────────

@description('FQDN for EventHub:Namespace in every appsettings.json (e.g. hashtagservice-eh.servicebus.windows.net).')
output ehNamespaceHost string = eventhubs.outputs.namespaceHost

@description('Derived storage account name.')
output storageAccountName string = storageAccountName

@description('CheckpointStorage:BlobUri for HashtagExtractor/appsettings.json.')
output extractorCheckpointBlobUri string = storage.outputs.extractorCheckpointBlobUri

@description('CheckpointStorage:BlobUri for HashtagPersister/appsettings.json.')
output persisterCheckpointBlobUri string = storage.outputs.persisterCheckpointBlobUri
