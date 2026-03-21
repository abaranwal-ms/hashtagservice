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
int checkpointInterval    = int.Parse(config["Service:CheckpointInterval"] ?? "10");

Console.WriteLine($"[HashtagPersister] Namespace:  {ehNamespace}");
Console.WriteLine($"[HashtagPersister] Topic:      {hashtagsTopic}");
Console.WriteLine($"[HashtagPersister] Cosmos:     {cosmosEndpoint} / {cosmosDatabaseName} / {hashtagsContainerName}");
Console.WriteLine($"[HashtagPersister] Checkpoint every {checkpointInterval} event(s)");

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

// Per-partition counters for checkpoint batching
var checkpointCounters = new ConcurrentDictionary<string, int>();
var processedCounters  = new ConcurrentDictionary<string, long>();

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
        // Read-modify-write: fetch existing doc or create new, increment count, upsert
        var doc = await hashtagHandler.GetAsync(hashtagCount.Hashtag, cts.Token)
            ?? new HashtagDocument { Hashtag = hashtagCount.Hashtag };

        doc.TotalPostCount += hashtagCount.Count;
        await hashtagHandler.UpdateAsync(doc, cts.Token);

        var pid = args.Partition.PartitionId;
        var total = processedCounters.AddOrUpdate(pid, 1, (_, c) => c + 1);

        // Checkpoint every N events per partition to amortise Blob Storage writes
        var counter = checkpointCounters.AddOrUpdate(pid, 1, (_, c) => c + 1);
        if (counter >= checkpointInterval)
        {
            await args.UpdateCheckpointAsync(cts.Token);
            checkpointCounters[pid] = 0;
            Console.WriteLine($"[Partition-{pid}] Checkpoint at {total} events — last: #{hashtagCount.Hashtag} (+{hashtagCount.Count})");
        }
    }
    catch (OperationCanceledException) { return; }
    catch (CosmosException ex)
    {
        Console.Error.WriteLine(
            $"[HashtagPersister] Cosmos error for '{hashtagCount.Hashtag}': {ex.StatusCode} — {ex.Message}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"[HashtagPersister] Error persisting '{hashtagCount.Hashtag}': {ex.Message}");
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

// Final checkpoint for each partition so we don't re-process on restart
Console.WriteLine("[HashtagPersister] Stopped.");
