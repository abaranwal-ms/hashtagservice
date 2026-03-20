using Azure.Identity;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Producer;
using Microsoft.Extensions.Configuration;
using HashtagService.Shared.Handlers;
using HashtagService.Shared.Models;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System;
using System.Collections.Generic;

namespace Shared.Tests;

public class EventHubFixture : IAsyncLifetime
{
    public EventHubProducerClient ProducerClient { get; private set; } = null!;

    // Config values stored for creating per-test consumer clients
    public string Namespace { get; private set; } = null!;
    public string Topic { get; private set; } = null!;
    public string ConsumerGroup { get; private set; } = null!;

    public Task InitializeAsync()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .Build();

        Namespace = config["EventHub:Namespace"]!;
        Topic = config["EventHub:PostsTopic"]!;
        ConsumerGroup = config["EventHub:ConsumerGroup"] ?? EventHubConsumerClient.DefaultConsumerGroupName;
        var credential = new DefaultAzureCredential();

        ProducerClient = new EventHubProducerClient(Namespace, Topic, credential);

        return Task.CompletedTask;
    }

    /// <summary>Creates a fresh consumer client — each test gets its own to avoid shared-cursor issues.</summary>
    public EventHubConsumerClient CreateConsumerClient()
    {
        return new EventHubConsumerClient(ConsumerGroup, Namespace, Topic, new DefaultAzureCredential());
    }

    public async Task DisposeAsync()
    {
        if (ProducerClient != null)
        {
            await ProducerClient.DisposeAsync();
        }
    }
}

[CollectionDefinition("EventHub")]
public class EventHubCollection : ICollectionFixture<EventHubFixture> {}

[Collection("EventHub")]
public class EventHubHandlerTests
{
    private readonly EventHubFixture _fixture;

    public EventHubHandlerTests(EventHubFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SendAndReceiveEvent_Works()
    {
        var producer = new EventHubProducerHandler(_fixture.ProducerClient);
        await using var consumerClient = _fixture.CreateConsumerClient();
        var consumer = new EventHubConsumerHandler(consumerClient);

        var testPost = new Post
        {
            Id = Guid.NewGuid(),
            Text = "Integration test message for Event Hub handler",
            CreatedAt = DateTimeOffset.UtcNow,
            Url = "https://example.com"
        };

        // 1. Capture current tail positions BEFORE sending (no race condition)
        var positions = await consumer.GetLatestPartitionPositionsAsync();

        // 2. Send the event
        await producer.SendEventAsync(testPost, testPost.Id.ToString());

        // 3. Read from the captured positions — the event will appear right away
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        bool found = false;

        try
        {
            await foreach (var post in consumer.ReadEventsFromPositionAsync<Post>(positions, cts.Token))
            {
                if (post.Id == testPost.Id)
                {
                    found = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* timeout — expected */ }

        Assert.True(found, "The test event was not received within the timeout period.");
    }

    [Fact]
    public async Task SendBatchAndReceive_Works()
    {
        var producer = new EventHubProducerHandler(_fixture.ProducerClient);
        await using var consumerClient = _fixture.CreateConsumerClient();
        var consumer = new EventHubConsumerHandler(consumerClient);

        var batchId = Guid.NewGuid().ToString();
        var testPosts = new List<Post>();
        for (int i = 0; i < 3; i++)
        {
            testPosts.Add(new Post
            {
                Id = Guid.NewGuid(),
                Text = $"Batch test {batchId} item {i}",
                CreatedAt = DateTimeOffset.UtcNow,
                Url = $"https://example.com/batch/{i}"
            });
        }

        // 1. Capture current tail positions BEFORE sending
        var positions = await consumer.GetLatestPartitionPositionsAsync();

        // 2. Send the batch
        await producer.SendEventsBatchAsync(testPosts, batchId);

        // 3. Read from the captured positions
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var receivedIds = new HashSet<Guid>();

        try
        {
            await foreach (var post in consumer.ReadEventsFromPositionAsync<Post>(positions, cts.Token))
            {
                if (testPosts.Any(p => p.Id == post.Id))
                {
                    receivedIds.Add(post.Id);
                    if (receivedIds.Count == testPosts.Count)
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }

        Assert.Equal(testPosts.Count, receivedIds.Count);
    }
}
