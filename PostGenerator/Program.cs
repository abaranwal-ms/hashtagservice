using Azure.Identity;
using Azure.Messaging.EventHubs.Producer;
using HashtagService.Shared.Handlers;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using PostGenerator;

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

// Create the producer and run threads
var producer = new PostProducer(postHandler, eventHubHandler);

var tasks = Enumerable.Range(1, threadCount).Select(threadId =>
    Task.Run(() => producer.RunAsync(threadId, postsPerBatch, intervalMs, cts.Token))).ToArray();

await Task.WhenAll(tasks);
Console.WriteLine("[PostGenerator] All producer threads stopped.");
