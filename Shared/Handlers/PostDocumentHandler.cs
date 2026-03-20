using System.Net;
using Microsoft.Azure.Cosmos;
using HashtagService.Shared.Models;

namespace HashtagService.Shared.Handlers;

/// <summary>
/// Interface for Cosmos DB entity handler for <see cref="PostDocument"/> in the Posts container.
/// </summary>
public interface IPostDocumentHandler
{
    Task<PostDocument> InsertAsync(PostDocument doc, CancellationToken ct = default);
    Task<PostDocument> UpdateAsync(PostDocument doc, CancellationToken ct = default);
    Task<PostDocument?> GetAsync(string postId, CancellationToken ct = default);
    Task<PostDocument> SoftDeleteAsync(string postId, CancellationToken ct = default);
    Task HardDeleteAsync(string postId, CancellationToken ct = default);
}

/// <summary>
/// Cosmos DB entity handler for <see cref="PostDocument"/> in the Posts container.
/// Partition key: /id (post GUID).
/// </summary>
public class PostDocumentHandler : IPostDocumentHandler
{
    private readonly Container _container;

    public PostDocumentHandler(Container container)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
    }

    /// <summary>Creates a new post document. Throws if it already exists.</summary>
    public async Task<PostDocument> InsertAsync(PostDocument doc, CancellationToken ct = default)
    {
        var response = await _container.CreateItemAsync(
            doc, new PartitionKey(doc.Id), cancellationToken: ct);
        return response.Resource;
    }

    /// <summary>Upserts (insert-or-replace) the post document.</summary>
    public async Task<PostDocument> UpdateAsync(PostDocument doc, CancellationToken ct = default)
    {
        var response = await _container.UpsertItemAsync(
            doc, new PartitionKey(doc.Id), cancellationToken: ct);
        return response.Resource;
    }

    /// <summary>Point-reads a post document by id. Returns null if not found.</summary>
    public async Task<PostDocument?> GetAsync(string postId, CancellationToken ct = default)
    {
        try
        {
            var response = await _container.ReadItemAsync<PostDocument>(
                postId, new PartitionKey(postId), cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Soft-deletes a post document by setting <c>IsDeleted = true</c> and stamping <c>DeletedAt</c>.
    /// </summary>
    public async Task<PostDocument> SoftDeleteAsync(string postId, CancellationToken ct = default)
    {
        var doc = await GetAsync(postId, ct)
            ?? throw new InvalidOperationException($"Post '{postId}' not found.");

        doc.IsDeleted = true;
        doc.DeletedAt = DateTimeOffset.UtcNow;

        var response = await _container.UpsertItemAsync(
            doc, new PartitionKey(doc.Id), cancellationToken: ct);
        return response.Resource;
    }

    /// <summary>Permanently removes the document from Cosmos DB (for test cleanup).</summary>
    public async Task HardDeleteAsync(string postId, CancellationToken ct = default)
    {
        try
        {
            await _container.DeleteItemAsync<PostDocument>(
                postId, new PartitionKey(postId), cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone — nothing to do.
        }
    }
}
