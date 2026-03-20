using System.Net;
using Microsoft.Azure.Cosmos;
using HashtagService.Shared.Models;

namespace HashtagService.Shared.Handlers;

/// <summary>
/// Cosmos DB entity handler for <see cref="HashtagDocument"/> in the Hashtags container.
/// Partition key: /hashtag (id == hashtag).
/// </summary>
public class HashtagDocumentHandler
{
    private readonly Container _container;

    public HashtagDocumentHandler(Container container)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
    }

    /// <summary>Creates a new hashtag document. Throws if it already exists.</summary>
    public async Task<HashtagDocument> InsertAsync(HashtagDocument doc, CancellationToken ct = default)
    {
        var response = await _container.CreateItemAsync(
            doc, new PartitionKey(doc.Hashtag), cancellationToken: ct);
        return response.Resource;
    }

    /// <summary>Upserts (insert-or-replace) the hashtag document.</summary>
    public async Task<HashtagDocument> UpdateAsync(HashtagDocument doc, CancellationToken ct = default)
    {
        var response = await _container.UpsertItemAsync(
            doc, new PartitionKey(doc.Hashtag), cancellationToken: ct);
        return response.Resource;
    }

    /// <summary>Point-reads a hashtag document. Returns null if not found.</summary>
    public async Task<HashtagDocument?> GetAsync(string hashtag, CancellationToken ct = default)
    {
        try
        {
            var response = await _container.ReadItemAsync<HashtagDocument>(
                hashtag, new PartitionKey(hashtag), cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Soft-deletes a hashtag document by setting <c>IsDeleted = true</c> and stamping <c>DeletedAt</c>.
    /// </summary>
    public async Task<HashtagDocument> SoftDeleteAsync(string hashtag, CancellationToken ct = default)
    {
        var doc = await GetAsync(hashtag, ct)
            ?? throw new InvalidOperationException($"Hashtag '{hashtag}' not found.");

        doc.IsDeleted = true;
        doc.DeletedAt = DateTimeOffset.UtcNow;

        var response = await _container.UpsertItemAsync(
            doc, new PartitionKey(doc.Hashtag), cancellationToken: ct);
        return response.Resource;
    }

    /// <summary>Permanently removes the document from Cosmos DB (for test cleanup).</summary>
    public async Task HardDeleteAsync(string hashtag, CancellationToken ct = default)
    {
        try
        {
            await _container.DeleteItemAsync<HashtagDocument>(
                hashtag, new PartitionKey(hashtag), cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone — nothing to do.
        }
    }
}
