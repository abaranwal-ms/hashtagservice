using Bogus;
using HashtagService.Shared.Handlers;
using HashtagService.Shared.Models;
using Moq;
using PostGenerator;

namespace PostGenerator.Tests;

/// <summary>
/// Tests for <see cref="PostProducer"/> with mocked Cosmos DB and Event Hub dependencies.
/// No Azure services required — all external calls are replaced with in-memory mocks.
/// </summary>
public class PostProducerTests
{
    private readonly Mock<IPostDocumentHandler> _mockPostHandler;
    private readonly Mock<IEventHubProducerHandler> _mockEventHubHandler;
    private readonly PostProducer _producer;
    private readonly List<PostDocument> _insertedDocuments = new();
    private readonly List<(object Payload, string? PartitionKey)> _sentEvents = new();

    public PostProducerTests()
    {
        _mockPostHandler = new Mock<IPostDocumentHandler>();
        _mockEventHubHandler = new Mock<IEventHubProducerHandler>();

        // Capture every Cosmos insert call
        _mockPostHandler
            .Setup(h => h.InsertAsync(It.IsAny<PostDocument>(), It.IsAny<CancellationToken>()))
            .Callback<PostDocument, CancellationToken>((doc, _) => _insertedDocuments.Add(doc))
            .ReturnsAsync((PostDocument doc, CancellationToken _) => doc);

        // Capture every Event Hub send call
        _mockEventHubHandler
            .Setup(h => h.SendEventAsync(It.IsAny<PostEvent>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<PostEvent, string?, CancellationToken>((evt, pk, _) => _sentEvents.Add((evt, pk)))
            .Returns(Task.CompletedTask);

        _producer = new PostProducer(_mockPostHandler.Object, _mockEventHubHandler.Object);
    }

    [Fact]
    public async Task ProduceBatch_CreatesCorrectNumberOfPosts()
    {
        var faker = PostProducer.CreateFaker();
        int batchSize = 5;

        var posts = await _producer.ProduceBatchAsync(faker, batchSize);

        Assert.Equal(batchSize, posts.Count);
        Assert.Equal(batchSize, _insertedDocuments.Count);
        Assert.Equal(batchSize, _sentEvents.Count);
    }

    [Fact]
    public async Task ProduceBatch_PostsHaveValidFields()
    {
        var faker = PostProducer.CreateFaker();

        var posts = await _producer.ProduceBatchAsync(faker, 3);

        foreach (var post in posts)
        {
            Assert.NotEqual(Guid.Empty, post.Id);
            Assert.False(string.IsNullOrWhiteSpace(post.UserId));
            Assert.StartsWith("https://", post.Url);
            Assert.False(string.IsNullOrWhiteSpace(post.Text));
            Assert.True(post.CreatedAt < DateTimeOffset.UtcNow.AddMinutes(1));
        }
    }

    [Fact]
    public async Task ProduceBatch_PostsContainAtLeastOneHashtag()
    {
        var faker = PostProducer.CreateFaker();

        var posts = await _producer.ProduceBatchAsync(faker, 10);

        foreach (var post in posts)
        {
            Assert.Contains("#", post.Text);
        }
    }

    [Fact]
    public async Task ProduceBatch_CosmosReceivesMatchingDocuments()
    {
        var faker = PostProducer.CreateFaker();

        var posts = await _producer.ProduceBatchAsync(faker, 3);

        for (int i = 0; i < posts.Count; i++)
        {
            var post = posts[i];
            var doc = _insertedDocuments[i];

            Assert.Equal(post.Id.ToString(), doc.Id);
            Assert.Equal(post.UserId, doc.UserId);
            Assert.Equal(post.Url, doc.Url);
            Assert.Equal(post.Text, doc.Text);
            Assert.False(doc.IsDeleted);
            Assert.Null(doc.DeletedAt);
        }
    }

    [Fact]
    public async Task ProduceBatch_EventHubReceivesPostIdAsPartitionKey()
    {
        var faker = PostProducer.CreateFaker();

        var posts = await _producer.ProduceBatchAsync(faker, 4);

        for (int i = 0; i < posts.Count; i++)
        {
            var (payload, partitionKey) = _sentEvents[i];
            var postEvent = (PostEvent)payload;

            Assert.Equal(posts[i].Id, postEvent.PostId);
            Assert.Equal(posts[i].Id.ToString(), partitionKey);
        }
    }

    [Fact]
    public async Task ProduceBatch_RespectsCallerBatchSize()
    {
        var faker = PostProducer.CreateFaker();

        var posts1 = await _producer.ProduceBatchAsync(faker, 1);
        Assert.Single(posts1);
        Assert.Single(_insertedDocuments);
        Assert.Single(_sentEvents);

        _insertedDocuments.Clear();
        _sentEvents.Clear();

        var posts7 = await _producer.ProduceBatchAsync(faker, 7);
        Assert.Equal(7, posts7.Count);
        Assert.Equal(7, _insertedDocuments.Count);
        Assert.Equal(7, _sentEvents.Count);
    }

    [Fact]
    public async Task ProduceBatch_CallsCosmosBeforeEventHub_PerPost()
    {
        // Verify ordering: for each post, Cosmos insert happens before Event Hub send
        var callOrder = new List<string>();

        var mockPost = new Mock<IPostDocumentHandler>();
        var mockEh = new Mock<IEventHubProducerHandler>();

        mockPost
            .Setup(h => h.InsertAsync(It.IsAny<PostDocument>(), It.IsAny<CancellationToken>()))
            .Callback<PostDocument, CancellationToken>((doc, _) => callOrder.Add($"cosmos:{doc.Id}"))
            .ReturnsAsync((PostDocument doc, CancellationToken _) => doc);

        mockEh
            .Setup(h => h.SendEventAsync(It.IsAny<PostEvent>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<PostEvent, string?, CancellationToken>((evt, _, _) => callOrder.Add($"eventhub:{evt.PostId}"))
            .Returns(Task.CompletedTask);

        var producer = new PostProducer(mockPost.Object, mockEh.Object);
        var faker = PostProducer.CreateFaker();

        var posts = await producer.ProduceBatchAsync(faker, 3);

        // Should alternate: cosmos, eventhub, cosmos, eventhub, cosmos, eventhub
        Assert.Equal(6, callOrder.Count);
        for (int i = 0; i < posts.Count; i++)
        {
            Assert.StartsWith("cosmos:", callOrder[i * 2]);
            Assert.StartsWith("eventhub:", callOrder[i * 2 + 1]);
        }
    }

    [Fact]
    public async Task ProduceBatch_StopsOnCancellation()
    {
        using var cts = new CancellationTokenSource();

        // Cancel after the 2nd Cosmos insert
        int insertCount = 0;
        var mockPost = new Mock<IPostDocumentHandler>();
        var mockEh = new Mock<IEventHubProducerHandler>();

        mockPost
            .Setup(h => h.InsertAsync(It.IsAny<PostDocument>(), It.IsAny<CancellationToken>()))
            .Callback<PostDocument, CancellationToken>((doc, _) =>
            {
                insertCount++;
                if (insertCount >= 2) cts.Cancel();
            })
            .ReturnsAsync((PostDocument doc, CancellationToken _) => doc);

        mockEh
            .Setup(h => h.SendEventAsync(It.IsAny<PostEvent>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var producer = new PostProducer(mockPost.Object, mockEh.Object);
        var faker = PostProducer.CreateFaker();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => producer.ProduceBatchAsync(faker, 10, cts.Token));

        // Should have processed 2 posts before cancellation kicked in on the 3rd
        Assert.True(insertCount >= 2 && insertCount <= 3);
    }

    [Fact]
    public async Task CreateFaker_GeneratesDiverseHashtags()
    {
        var faker = PostProducer.CreateFaker();
        var allHashtags = new HashSet<string>();

        for (int i = 0; i < 50; i++)
        {
            var post = faker.Generate();
            var words = post.Text.Split(' ');
            foreach (var w in words.Where(w => w.StartsWith('#')))
            {
                allHashtags.Add(w.ToLower());
            }
        }

        // With 50 posts, we should see a variety of hashtags
        Assert.True(allHashtags.Count >= 3, $"Expected diverse hashtags but only got {allHashtags.Count}: {string.Join(", ", allHashtags)}");
    }

    [Fact]
    public async Task ProduceBatch_ZeroBatchSize_ReturnsEmptyList()
    {
        var faker = PostProducer.CreateFaker();

        var posts = await _producer.ProduceBatchAsync(faker, 0);

        Assert.Empty(posts);
        _mockPostHandler.Verify(h => h.InsertAsync(It.IsAny<PostDocument>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockEventHubHandler.Verify(h => h.SendEventAsync(It.IsAny<PostEvent>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
