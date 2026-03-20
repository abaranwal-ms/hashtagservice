using Azure.Identity;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Processor;
using Azure.Messaging.EventHubs.Producer;
using Azure.Storage.Blobs;
using HashtagExtractor;
using HashtagService.Shared.Handlers;
using HashtagService.Shared.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using System.Text.Json;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var ehNamespace   = config["EventHub:Namespace"]!;
var postsTopic    = config["EventHub:PostsTopic"]!;
var hashtagsTopic = config["EventHub:HashtagsTopic"]!;
var consumerGroup = config["EventHub:ConsumerGroup"] ?? "$Default";
var checkpointUri = config["CheckpointStorage:BlobUri"]!;
int batchSize     = int.Parse(config["Service:BatchSize"] ?? "100");
int maxBatchSize  = int.Parse(config["Service:MaxBatchSize"] ?? "500");

var cosmosEndpoint      = config["CosmosDb:Endpoint"]!;
var cosmosDatabaseName  = config["CosmosDb:DatabaseName"]!;
var postsContainerName  = config["CosmosDb:PostsContainerName"]!;

Console.WriteLine($"[HashtagExtractor] Namespace:     {ehNamespace}");
Console.WriteLine($"[HashtagExtractor] Posts topic:    {postsTopic}");
Console.WriteLine($"[HashtagExtractor] Hashtags topic: {hashtagsTopic}");
Console.WriteLine($"[HashtagExtractor] Cosmos:         {cosmosEndpoint} / {cosmosDatabaseName} / {postsContainerName}");
Console.WriteLine($"[HashtagExtractor] BatchSize={batchSize}, MaxBatchSize={maxBatchSize}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Cosmos DB client for fetching post data (thread-safe)
var cosmosClient = new CosmosClient(cosmosEndpoint, new DefaultAzureCredential(),
    new CosmosClientOptions
    {
        SerializerOptions = new CosmosSerializationOptions
        {
            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
        }
    });
var postsContainer = cosmosClient.GetContainer(cosmosDatabaseName, postsContainerName);
var postHandler = new PostDocumentHandler(postsContainer);

// Shared producer for hashtags-topic (thread-safe per SDK docs)
await using var hashtagProducer = new EventHubProducerClient(
    ehNamespace, hashtagsTopic, new DefaultAzureCredential());

var producerHandler = new EventHubProducerHandler(hashtagProducer);
var publisher = new KafkaHashtagPublisher(producerHandler);

// One PartitionConsumer per partition, created on first event
var partitionConsumers = new ConcurrentDictionary<string, PartitionConsumer>();

// Blob storage for distributed checkpointing
var storageClient = new BlobContainerClient(new Uri(checkpointUri), new DefaultAzureCredential());

// EventProcessorClient: partition-balanced, consumer-group-based.
// Multiple instances of this service each pick up different partitions automatically.
var processor = new EventProcessorClient(
    storageClient, consumerGroup, ehNamespace, postsTopic, new DefaultAzureCredential());

processor.ProcessEventAsync += async args =>
{
    if (!args.HasEvent) return;

    PostNotification? notification;
    try
    {
        notification = JsonSerializer.Deserialize<PostNotification>(args.Data.Body.ToArray());
    }
    catch (JsonException ex)
    {
        Console.Error.WriteLine($"[HashtagExtractor] Deserialization error: {ex.Message}");
        return;
    }

    if (notification is null) return;

    var consumer = partitionConsumers.GetOrAdd(
        args.Partition.PartitionId,
        pid => new PartitionConsumer(pid, publisher, postHandler, batchSize, maxBatchSize));

    await consumer.ProcessEventAsync(notification.PostId, args, cts.Token);
};

processor.ProcessErrorAsync += args =>
{
    Console.Error.WriteLine(
        $"[HashtagExtractor] Partition {args.PartitionId} error ({args.Operation}): {args.Exception.Message}");
    return Task.CompletedTask;
};

await processor.StartProcessingAsync(cts.Token);
Console.WriteLine("[HashtagExtractor] Processor started. Press Ctrl+C to stop.");

try { await Task.Delay(Timeout.Infinite, cts.Token); }
catch (OperationCanceledException) { }

Console.WriteLine("[HashtagExtractor] Shutting down...");
await processor.StopProcessingAsync();

// Flush remaining counts from all partition consumers
using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
foreach (var (pid, consumer) in partitionConsumers)
{
    try
    {
        await consumer.FlushRemainingAsync(shutdownCts.Token);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[HashtagExtractor] Flush error for partition {pid}: {ex.Message}");
    }
}

Console.WriteLine("[HashtagExtractor] Stopped.");
