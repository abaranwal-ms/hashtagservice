// ─────────────────────────────────────────────────────────────────────────────
// Module: eventhubs.bicep
//
// Provisions:
//   • Event Hubs namespace  (Standard tier, Kafka-enabled, 1 TU)
//   • posts-topic           (3 partitions, 1-day retention)
//   • hashtags-topic        (3 partitions, 1-day retention)
//   • $Default consumer group on each topic (explicit so Bicep owns state)
// ─────────────────────────────────────────────────────────────────────────────

@description('Azure region for all resources.')
param location string

@description('Globally unique Event Hubs namespace name.')
param namespaceName string

@description('Resource tags applied to every resource in this module.')
param tags object = {}

// ─── Namespace ────────────────────────────────────────────────────────────────
// Standard tier: required for >1 consumer group, Kafka protocol support,
// and up to 100 Event Hubs per namespace.
resource ehNamespace 'Microsoft.EventHub/namespaces@2021-11-01' = {
  name: namespaceName
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Standard'
    capacity: 1   // 1 Throughput Unit – ample for ~3-4 events/sec (see docs/kafka-setup-spec.md)
  }
  properties: {
    isAutoInflateEnabled: false   // keep cost predictable; enable manually if TU is exhausted
    maximumThroughputUnits: 0
    zoneRedundant: false          // not needed for a learning/dev namespace
  }
}

// ─── posts-topic ──────────────────────────────────────────────────────────────
// Producer : PostGenerator (PostCreator)
// Consumer : HashtagExtractor (HashTagCounter)
resource postsTopic 'Microsoft.EventHub/namespaces/eventhubs@2021-11-01' = {
  parent: ehNamespace
  name: 'posts-topic'
  properties: {
    partitionCount: 3              // matches Service:ThreadCount = 3 in PostGenerator
    messageRetentionInDays: 1      // minimum; extend if replay is needed
  }
}

// ─── hashtags-topic ───────────────────────────────────────────────────────────
// Producer : HashtagExtractor (HashTagCounter)
// Consumer : HashtagPersister
resource hashtagsTopic 'Microsoft.EventHub/namespaces/eventhubs@2021-11-01' = {
  parent: ehNamespace
  name: 'hashtags-topic'
  properties: {
    partitionCount: 3
    messageRetentionInDays: 1
  }
}

// ─── Consumer groups ─────────────────────────────────────────────────────────
// Azure auto-creates $Default on each Event Hub, but declaring it explicitly
// makes Bicep the authoritative owner of the resource and avoids drift.
// Add extra consumer groups here if additional independent readers are needed.
resource postsTopicDefaultCg 'Microsoft.EventHub/namespaces/eventhubs/consumergroups@2021-11-01' = {
  parent: postsTopic
  name: '$Default'
}

resource hashtagsTopicDefaultCg 'Microsoft.EventHub/namespaces/eventhubs/consumergroups@2021-11-01' = {
  parent: hashtagsTopic
  name: '$Default'
}

// ─── Outputs ─────────────────────────────────────────────────────────────────
@description('Resource ID of the Event Hubs namespace.')
output namespaceId string = ehNamespace.id

@description('Fully-qualified FQDN used as the EventHub:Namespace config value.')
output namespaceHost string = '${ehNamespace.name}.servicebus.windows.net'
