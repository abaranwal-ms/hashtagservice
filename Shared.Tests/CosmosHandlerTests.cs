using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using HashtagService.Shared.Handlers;
using HashtagService.Shared.Models;

namespace Shared.Tests;

/// <summary>
/// Shared Cosmos DB fixture — creates a single <see cref="CosmosClient"/> and
/// container references for the lifetime of the test collection.
/// </summary>
public class CosmosFixture : IAsyncLifetime
{
    public Container HashtagsContainer { get; private set; } = null!;
    public Container PostsContainer { get; private set; } = null!;

    private CosmosClient _client = null!;

    public Task InitializeAsync()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .Build();

        _client = new CosmosClient(
            config["CosmosDb:Endpoint"],
            new DefaultAzureCredential(),
            new CosmosClientOptions
            {
                SerializerOptions = new CosmosSerializationOptions
                {
                    PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
                }
            });

        var db = config["CosmosDb:DatabaseName"]!;
        HashtagsContainer = _client.GetContainer(db, config["CosmosDb:HashtagsContainerName"]!);
        PostsContainer = _client.GetContainer(db, config["CosmosDb:PostsContainerName"]!);

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }
}

[CollectionDefinition("Cosmos")]
public class CosmosCollection : ICollectionFixture<CosmosFixture> { }

// ──────────────────────────────────────────────────────────────────────────────
// HashtagDocument Tests
// ──────────────────────────────────────────────────────────────────────────────

[Collection("Cosmos")]
public class HashtagDocumentHandlerTests : IAsyncLifetime
{
    private readonly HashtagDocumentHandler _handler;
    private readonly List<string> _createdHashtags = new();

    public HashtagDocumentHandlerTests(CosmosFixture fixture)
    {
        _handler = new HashtagDocumentHandler(fixture.HashtagsContainer);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Cleanup: hard-delete any documents created during the test run.</summary>
    public async Task DisposeAsync()
    {
        foreach (var tag in _createdHashtags)
        {
            try
            {
                await _handler.HardDeleteAsync(tag);
            }
            catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task Insert_CreatesDocument()
    {
        var tag = $"test-insert-{Guid.NewGuid():N}"[..40];
        _createdHashtags.Add(tag);

        var doc = new HashtagDocument
        {
            Hashtag = tag,
            TotalPostCount = 1,
            TopPosts = new List<HashtagPostRef>
            {
                new() { PostId = Guid.NewGuid().ToString(), ImageUrl = "https://cdn.example.com/img/test.jpg" }
            }
        };

        var created = await _handler.InsertAsync(doc);

        Assert.Equal(tag, created.Hashtag);
        Assert.Equal(1, created.TotalPostCount);
        Assert.False(created.IsDeleted);
        Assert.Null(created.DeletedAt);
    }

    [Fact]
    public async Task Get_ReturnsInsertedDocument()
    {
        var tag = $"test-get-{Guid.NewGuid():N}"[..40];
        _createdHashtags.Add(tag);

        var doc = new HashtagDocument { Hashtag = tag, TotalPostCount = 5 };
        await _handler.InsertAsync(doc);

        var fetched = await _handler.GetAsync(tag);

        Assert.NotNull(fetched);
        Assert.Equal(tag, fetched!.Hashtag);
        Assert.Equal(5, fetched.TotalPostCount);
    }

    [Fact]
    public async Task Get_ReturnsNull_WhenNotFound()
    {
        var result = await _handler.GetAsync("nonexistent-hashtag-xyz");
        Assert.Null(result);
    }

    [Fact]
    public async Task Update_ModifiesExistingDocument()
    {
        var tag = $"test-update-{Guid.NewGuid():N}"[..40];
        _createdHashtags.Add(tag);

        var doc = new HashtagDocument { Hashtag = tag, TotalPostCount = 1 };
        await _handler.InsertAsync(doc);

        doc.TotalPostCount = 42;
        doc.TopPosts.Add(new HashtagPostRef
        {
            PostId = Guid.NewGuid().ToString(),
            ImageUrl = "https://cdn.example.com/img/updated.jpg"
        });

        var updated = await _handler.UpdateAsync(doc);

        Assert.Equal(42, updated.TotalPostCount);
        Assert.Single(updated.TopPosts);
    }

    [Fact]
    public async Task SoftDelete_SetsFlags()
    {
        var tag = $"test-delete-{Guid.NewGuid():N}"[..40];
        _createdHashtags.Add(tag);

        var doc = new HashtagDocument { Hashtag = tag, TotalPostCount = 10 };
        await _handler.InsertAsync(doc);

        var deleted = await _handler.SoftDeleteAsync(tag);

        Assert.True(deleted.IsDeleted);
        Assert.NotNull(deleted.DeletedAt);

        // Verify persisted
        var fetched = await _handler.GetAsync(tag);
        Assert.NotNull(fetched);
        Assert.True(fetched!.IsDeleted);
    }

    [Fact]
    public async Task SoftDelete_ThrowsWhenNotFound()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _handler.SoftDeleteAsync("nonexistent-hashtag-xyz"));
    }
}

// ──────────────────────────────────────────────────────────────────────────────
// PostDocument Tests
// ──────────────────────────────────────────────────────────────────────────────

[Collection("Cosmos")]
public class PostDocumentHandlerTests : IAsyncLifetime
{
    private readonly PostDocumentHandler _handler;
    private readonly List<string> _createdPostIds = new();

    public PostDocumentHandlerTests(CosmosFixture fixture)
    {
        _handler = new PostDocumentHandler(fixture.PostsContainer);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Cleanup: hard-delete any documents created during the test run.</summary>
    public async Task DisposeAsync()
    {
        foreach (var id in _createdPostIds)
        {
            try
            {
                await _handler.HardDeleteAsync(id);
            }
            catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task Insert_CreatesDocument()
    {
        var id = Guid.NewGuid().ToString();
        _createdPostIds.Add(id);

        var doc = new PostDocument
        {
            Id = id,
            UserId = "user-test-1",
            Url = "https://cdn.example.com/img/test.jpg",
            Text = "Hello #world from the #test",
            CreatedAt = DateTimeOffset.UtcNow
        };

        var created = await _handler.InsertAsync(doc);

        Assert.Equal(id, created.Id);
        Assert.Equal("user-test-1", created.UserId);
        Assert.False(created.IsDeleted);
        Assert.Null(created.DeletedAt);
    }

    [Fact]
    public async Task Get_ReturnsInsertedDocument()
    {
        var id = Guid.NewGuid().ToString();
        _createdPostIds.Add(id);

        var doc = new PostDocument
        {
            Id = id,
            UserId = "user-test-2",
            Url = "https://cdn.example.com/img/get.jpg",
            Text = "Testing get #query",
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _handler.InsertAsync(doc);

        var fetched = await _handler.GetAsync(id);

        Assert.NotNull(fetched);
        Assert.Equal(id, fetched!.Id);
        Assert.Equal("user-test-2", fetched.UserId);
    }

    [Fact]
    public async Task Get_ReturnsNull_WhenNotFound()
    {
        var result = await _handler.GetAsync(Guid.NewGuid().ToString());
        Assert.Null(result);
    }

    [Fact]
    public async Task Update_ModifiesExistingDocument()
    {
        var id = Guid.NewGuid().ToString();
        _createdPostIds.Add(id);

        var doc = new PostDocument
        {
            Id = id,
            UserId = "user-test-3",
            Url = "https://cdn.example.com/img/original.jpg",
            Text = "Original #text",
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _handler.InsertAsync(doc);

        doc.Text = "Updated #text with #more hashtags";
        doc.Url = "https://cdn.example.com/img/updated.jpg";

        var updated = await _handler.UpdateAsync(doc);

        Assert.Equal("Updated #text with #more hashtags", updated.Text);
        Assert.Equal("https://cdn.example.com/img/updated.jpg", updated.Url);
    }

    [Fact]
    public async Task SoftDelete_SetsFlags()
    {
        var id = Guid.NewGuid().ToString();
        _createdPostIds.Add(id);

        var doc = new PostDocument
        {
            Id = id,
            UserId = "user-test-4",
            Url = "https://cdn.example.com/img/delete.jpg",
            Text = "Will be deleted #goodbye",
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _handler.InsertAsync(doc);

        var deleted = await _handler.SoftDeleteAsync(id);

        Assert.True(deleted.IsDeleted);
        Assert.NotNull(deleted.DeletedAt);

        // Verify persisted
        var fetched = await _handler.GetAsync(id);
        Assert.NotNull(fetched);
        Assert.True(fetched!.IsDeleted);
    }

    [Fact]
    public async Task SoftDelete_ThrowsWhenNotFound()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _handler.SoftDeleteAsync(Guid.NewGuid().ToString()));
    }
}