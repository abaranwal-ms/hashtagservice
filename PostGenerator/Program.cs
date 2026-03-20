using Azure.Identity;
using Azure.Messaging.EventHubs.Producer;
using Bogus;
using HashtagService.Shared.Handlers;
using HashtagService.Shared.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var ehNamespace    = config["EventHub:Namespace"]!;
var postsTopic     = config["EventHub:PostsTopic"]!;
var cosmosEndpoint = config["CosmosDb:Endpoint"]!;
var dbName         = config["CosmosDb:DatabaseName"]!;
var containerName  = config["CosmosDb:PostsContainerName"]!;
int threadCount    = int.Parse(config["Service:ThreadCount"] ?? "3");
int postsPerBatch  = int.Parse(config["Service:PostsPerBatch"] ?? "5");
int intervalMs     = int.Parse(config["Service:IntervalMs"] ?? "2000");

Console.WriteLine($"[PostGenerator] Starting {threadCount} producer threads → {ehNamespace}/{postsTopic}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var credential = new DefaultAzureCredential();

// Cosmos DB client + handler (both thread-safe, shared across threads)
using var cosmosClient = new CosmosClient(cosmosEndpoint, credential, new CosmosClientOptions
{
    SerializerOptions = new CosmosSerializationOptions
    {
        PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
    }
});
var postsContainer = cosmosClient.GetContainer(dbName, containerName);
var postHandler    = new PostDocumentHandler(postsContainer);

// Event Hub producer client (thread-safe, shared)
await using var producerClient = new EventHubProducerClient(ehNamespace, postsTopic, credential);
var eventHubHandler = new EventHubProducerHandler(producerClient);

var tasks = Enumerable.Range(1, threadCount).Select(threadId =>
    Task.Run(() => ProduceAsync(postHandler, eventHubHandler, threadId, postsPerBatch, intervalMs, cts.Token))).ToArray();

await Task.WhenAll(tasks);
Console.WriteLine("[PostGenerator] All producer threads stopped.");

static async Task ProduceAsync(
    PostDocumentHandler postHandler,
    EventHubProducerHandler eventHubHandler,
    int threadId,
    int batchSize,
    int intervalMs,
    CancellationToken ct)
{
    // Bogus Faker is not thread-safe; create one instance per thread
    var faker = new Faker<Post>()
        .RuleFor(p => p.Id, f => f.Random.Guid())
        .RuleFor(p => p.UserId, f => f.Internet.UserName())
        .RuleFor(p => p.Url, f => $"https://{f.Internet.DomainName()}/content/{f.Random.AlphaNumeric(12)}")
        .RuleFor(p => p.Text, f =>
        {
            var words = f.Lorem.Words(f.Random.Number(7, 11)).ToList();
            int hashtagCount = f.Random.Number(1, 3);
            var indices = Enumerable.Range(0, words.Count).OrderBy(_ => f.Random.Double()).Take(hashtagCount);
            foreach (var idx in indices)
            {
                words[idx] = $"#{f.Hacker.Noun().Replace(" ", "")}";
            }
            return string.Join(" ", words);
        })
        .RuleFor(p => p.CreatedAt, f => f.Date.RecentOffset(days: 1));

    long total = 0;

    while (!ct.IsCancellationRequested)
    {
        int sent = 0;

        for (int i = 0; i < batchSize; i++)
        {
            var post = faker.Generate();

            // 1. Persist the full post document to Cosmos DB
            await postHandler.InsertAsync(PostDocument.FromPost(post), ct);

            // 2. Emit a PostEvent (post ID only) to Event Hub
            var postEvent = new PostEvent { PostId = post.Id };
            await eventHubHandler.SendEventAsync(postEvent, post.Id.ToString(), ct);

            sent++;
        }

        total += sent;
        Console.WriteLine($"[Thread-{threadId:D2}] Sent batch of {sent} posts (total: {total})");

        await Task.Delay(intervalMs, ct);
    }
}
