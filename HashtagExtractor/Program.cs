using Azure.Identity;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Processor;
using Azure.Messaging.EventHubs.Producer;
using Azure.Storage.Blobs;
using HashtagService.Shared.Models;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var ehNamespace    = config["EventHub:Namespace"]!;
var postsTopic     = config["EventHub:PostsTopic"]!;
var hashtagsTopic  = config["EventHub:HashtagsTopic"]!;
var consumerGroup  = config["EventHub:ConsumerGroup"] ?? "$Default";
var checkpointUri  = config["CheckpointStorage:BlobUri"]!;
int threadCount    = int.Parse(config["Service:ThreadCount"] ?? "3");

Console.WriteLine($"[HashtagExtractor] Listening on {ehNamespace}/{postsTopic} → publishing to {hashtagsTopic}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Shared producer: publish extracted hashtag events to hashtags-topic
await using var hashtagProducer = new EventHubProducerClient(
    ehNamespace, hashtagsTopic, new DefaultAzureCredential());

// EventProcessorClient uses blob storage for distributed checkpointing.
// Multiple instances of this service will each pick up different partitions automatically.
var storageClient = new BlobContainerClient(new Uri(checkpointUri), new DefaultAzureCredential());
var processor = new EventProcessorClient(
    storageClient, consumerGroup, ehNamespace, postsTopic, new DefaultAzureCredential());

// Semaphore to limit concurrent partition processing to ThreadCount
var semaphore = new SemaphoreSlim(threadCount, threadCount);

processor.ProcessEventAsync += async args =>
{
    await semaphore.WaitAsync(cts.Token);
    try
    {
        await HandlePostEventAsync(args, hashtagProducer, cts.Token);
    }
    finally
    {
        semaphore.Release();
    }
};

processor.ProcessErrorAsync += args =>
{
    Console.Error.WriteLine($"[HashtagExtractor] Partition {args.PartitionId} error: {args.Exception.Message}");
    return Task.CompletedTask;
};

await processor.StartProcessingAsync(cts.Token);
Console.WriteLine("[HashtagExtractor] Processor started. Press Ctrl+C to stop.");

try { await Task.Delay(Timeout.Infinite, cts.Token); }
catch (OperationCanceledException) { }

await processor.StopProcessingAsync();
Console.WriteLine("[HashtagExtractor] Processor stopped.");

static async Task HandlePostEventAsync(
    ProcessEventArgs args,
    EventHubProducerClient producer,
    CancellationToken ct)
{
    if (!args.HasEvent)
        return;

    Post? post;
    try
    {
        post = JsonSerializer.Deserialize<Post>(args.Data.Body.ToArray());
    }
    catch (JsonException ex)
    {
        Console.Error.WriteLine($"[HashtagExtractor] Deserialization error: {ex.Message}");
        return;
    }

    if (post is null)
        return;

    var hashtags = ExtractHashtags(post.Text);
    if (hashtags.Count == 0)
    {
        await args.UpdateCheckpointAsync(ct);
        return;
    }

    // One send per hashtag is intentional: each hashtag is a distinct partition key,
    // so events for different hashtags cannot share a batch.  Grouping by key within
    // a single-post handler would yield the same N sends for N distinct hashtags.
    var extractedAt = DateTimeOffset.UtcNow;
    foreach (var hashtag in hashtags)
    {
        var hashtagEvent = new HashtagEvent
        {
            PostId      = post.Id,
            Hashtags    = new List<string> { hashtag },
            ExtractedAt = extractedAt
        };

        using var batch = await producer.CreateBatchAsync(
            new CreateBatchOptions { PartitionKey = hashtag }, ct);
        batch.TryAdd(new EventData(JsonSerializer.Serialize(hashtagEvent)));
        await producer.SendAsync(batch, ct);
    }

    Console.WriteLine($"[Partition-{args.Partition.PartitionId}] Post {post.Id} → hashtags: {string.Join(", ", hashtags)}");

    await args.UpdateCheckpointAsync(ct);
}

static List<string> ExtractHashtags(string text)
{
    return text
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(w => w.StartsWith('#') && w.Length > 1)
        .Select(w => w.TrimStart('#').ToLowerInvariant())
        .Distinct()
        .ToList();
}
