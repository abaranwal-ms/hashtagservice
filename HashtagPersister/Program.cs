using Azure.Identity;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Processor;
using Azure.Storage.Blobs;
using HashtagService.Shared.Handlers;
using HashtagService.Shared.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using System.Text.Json;
using HashtagPersister;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var ehNamespace           = config["EventHub:Namespace"]!;
var hashtagsTopic         = config["EventHub:HashtagsTopic"]!;
var consumerGroup         = config["EventHub:ConsumerGroup"] ?? "$Default";
var checkpointUri         = config["CheckpointStorage:BlobUri"]!;
var cosmosEndpoint        = config["CosmosDb:Endpoint"]!;
var cosmosDatabaseName    = config["CosmosDb:DatabaseName"]!;
var hashtagsContainerName = config["CosmosDb:HashtagsContainerName"]!;
int batchSize             = int.Parse(config["Service:BatchSize"] ?? "50");
int maxBatchSize          = int.Parse(config["Service:MaxBatchSize"] ?? "250");
int maxFlushConcurrency   = int.Parse(config["Service:MaxFlushConcurrency"] ?? "5");

Console.WriteLine($"[HashtagPersister] Namespace:  {ehNamespace}");
Console.WriteLine($"[HashtagPersister] Topic:      {hashtagsTopic}");
Console.WriteLine($"[HashtagPersister] Cosmos:     {cosmosEndpoint} / {cosmosDatabaseName} / {hashtagsContainerName}");
Console.WriteLine($"[HashtagPersister] Batch:      {batchSize} events (swap), max {maxBatchSize} (hard block)");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Cosmos DB client (thread-safe, shared)
var cosmosClient = new CosmosClient(cosmosEndpoint, new DefaultAzureCredential(),
    new CosmosClientOptions
    {
        SerializerOptions = new CosmosSerializationOptions
        {
            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
        }
    });
var hashtagsContainer = cosmosClient.GetContainer(cosmosDatabaseName, hashtagsContainerName);
var hashtagHandler = new HashtagDocumentHandler(hashtagsContainer);

// Blob storage for distributed checkpointing
var storageClient = new BlobContainerClient(new Uri(checkpointUri), new DefaultAzureCredential());

// Per-partition double-buffer: reader accumulates into read dict,
// background writer flushes write dict to Cosmos concurrently.
var partitionBuffers = new ConcurrentDictionary<string, PartitionBuffer>();

PartitionBuffer GetOrCreateBuffer(string partitionId)
    => partitionBuffers.GetOrAdd(partitionId, pid =>
        new PartitionBuffer(pid, hashtagHandler, batchSize, maxBatchSize, maxFlushConcurrency));

// EventProcessorClient: reads all partitions of hashtags-topic,
// auto-balanced across instances via consumer group.
var processor = new EventProcessorClient(
    storageClient, consumerGroup, ehNamespace, hashtagsTopic, new DefaultAzureCredential());

processor.ProcessEventAsync += async args =>
{
    if (!args.HasEvent) return;

    HashtagCount? hashtagCount;
    try
    {
        hashtagCount = JsonSerializer.Deserialize<HashtagCount>(args.Data.Body.ToArray());
    }
    catch (JsonException ex)
    {
        Console.Error.WriteLine($"[HashtagPersister] Deserialization error: {ex.Message}");
        return;
    }

    if (hashtagCount is null || string.IsNullOrEmpty(hashtagCount.Hashtag))
    {
        Console.Error.WriteLine("[HashtagPersister] Received null or empty hashtag, skipping");
        return;
    }

    try
    {
        var buf = GetOrCreateBuffer(args.Partition.PartitionId);

        // Accumulate into read dict; swap + background flush happens inside
        // when BatchSize is reached. Checkpoint func is captured here and
        // called by the writer *after* Cosmos writes complete.
        await buf.AccumulateAsync(
            hashtagCount.Hashtag,
            hashtagCount.Count,
            ct => args.UpdateCheckpointAsync(ct),
            cts.Token);
    }
    catch (OperationCanceledException) { return; }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"[HashtagPersister] Error processing '{hashtagCount.Hashtag}': {ex.Message}");
    }
};

processor.ProcessErrorAsync += args =>
{
    Console.Error.WriteLine(
        $"[HashtagPersister] Partition {args.PartitionId} error ({args.Operation}): {args.Exception.Message}");
    return Task.CompletedTask;
};

try
{
    await processor.StartProcessingAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[HashtagPersister] Failed to start processor: {ex}");
    return;
}
Console.WriteLine("[HashtagPersister] Processor started. Press Ctrl+C to stop.");

try { await Task.Delay(Timeout.Infinite, cts.Token); }
catch (OperationCanceledException) { }

Console.WriteLine("[HashtagPersister] Shutting down...");
await processor.StopProcessingAsync();

// Flush any remaining read buffers (waits for in-progress writers first)
foreach (var (pid, buf) in partitionBuffers)
{
    try
    {
        await buf.FlushRemainingAsync(CancellationToken.None);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"[HashtagPersister] Error during final flush of partition {pid}: {ex.Message}");
    }
}

Console.WriteLine("[HashtagPersister] Stopped.");
