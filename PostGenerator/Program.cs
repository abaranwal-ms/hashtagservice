using Azure.Identity;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using HashtagService.Shared.Models;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var ehNamespace = config["EventHub:Namespace"]!;
var postsTopic  = config["EventHub:PostsTopic"]!;
int threadCount = int.Parse(config["Service:ThreadCount"] ?? "3");
int postsPerBatch = int.Parse(config["Service:PostsPerBatch"] ?? "5");
int intervalMs  = int.Parse(config["Service:IntervalMs"] ?? "2000");

Console.WriteLine($"[PostGenerator] Starting {threadCount} producer threads → {ehNamespace}/{postsTopic}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Shared producer client (thread-safe)
await using var producer = new EventHubProducerClient(ehNamespace, postsTopic, new DefaultAzureCredential());

var tasks = Enumerable.Range(1, threadCount).Select(threadId =>
    Task.Run(() => ProduceAsync(producer, threadId, postsPerBatch, intervalMs, cts.Token))).ToArray();

await Task.WhenAll(tasks);
Console.WriteLine("[PostGenerator] All producer threads stopped.");

static async Task ProduceAsync(
    EventHubProducerClient producer,
    int threadId,
    int batchSize,
    int intervalMs,
    CancellationToken ct)
{
    var rng = new Random(threadId);
    long total = 0;

    while (!ct.IsCancellationRequested)
    {
        int sent = 0;

        // One send per post is intentional: every post has a unique Id, so events
        // cannot share a partition key and therefore cannot be combined into a
        // single batch.  The per-event overhead is acceptable at dev-scale throughput.
        for (int i = 0; i < batchSize; i++)
        {
            var post    = PostFactory.Create(rng);
            var json    = JsonSerializer.Serialize(post);
            var options = new CreateBatchOptions { PartitionKey = post.Id.ToString() };
            using var batch = await producer.CreateBatchAsync(options, ct);
            batch.TryAdd(new EventData(json));
            await producer.SendAsync(batch, ct);
            sent++;
        }

        total += sent;
        Console.WriteLine($"[Thread-{threadId:D2}] Sent batch of {sent} posts (total: {total})");

        await Task.Delay(intervalMs, ct);
    }
}

static class PostFactory
{
    // ~8-char English words commonly found in social posts
    private static readonly string[] Words =
    {
        "amazing", "beautiful", "creative", "discover", "exciting",
        "fabulous", "grateful", "inspired", "journeys", "kindness",
        "learning", "memories", "naturally", "optimize", "peaceful",
        "reaching", "stunning", "thriving", "ultimate", "vibrant",
        "wandered", "xcellent", "yearning", "zealously", "adventur",
        "blossome", "captured", "dreaming", "energize", "flourish",
        "grateful", "highland", "imagined", "jubilant", "launched",
        "manifest", "northern", "overcome", "pathways", "question",
        "radiance", "selected", "together", "unfolded", "victorious"
    };

    // Words that will be prefixed with '#' to act as hashtags
    private static readonly string[] HashtagWords =
    {
        "trending", "techlife", "startup", "innovate", "cloudtech",
        "datadrive", "devlife", "codecraft", "buildinpublic", "shipping"
    };

    private static readonly string[] CdnHosts =
    {
        "cdn.example.com", "media.socialapp.io", "assets.postnet.dev",
        "static.sharehub.co", "content.buzzfeed.net"
    };

    public static Post Create(Random rng)
    {
        var contentId = Guid.NewGuid().ToString("N")[..12];
        var host = CdnHosts[rng.Next(CdnHosts.Length)];
        var url = $"https://{host}/content/{contentId}";

        // Pick ~10 words, sprinkle 2-3 hashtags
        var wordCount = rng.Next(8, 12);
        var textWords = Enumerable.Range(0, wordCount)
            .Select(_ => rng.NextDouble() < 0.25
                ? $"#{HashtagWords[rng.Next(HashtagWords.Length)]}"
                : Words[rng.Next(Words.Length)])
            .ToList();

        return new Post
        {
            Id        = Guid.NewGuid(),
            Url       = url,
            Text      = string.Join(" ", textWords),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}
