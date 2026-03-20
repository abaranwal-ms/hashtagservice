using System.Text.Json.Serialization;

namespace HashtagService.Shared.Models;

/// <summary>
/// Cosmos DB document for the <c>Hashtags</c> container.
/// Partition key: /hashtag. One document per unique hashtag.
/// </summary>
public class HashtagDocument
{
    /// <summary>
    /// Cosmos DB document id — always equal to <see cref="Hashtag"/>.
    /// Computed property; set <see cref="Hashtag"/> and this follows.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id => Hashtag;

    /// <summary>The hashtag (lowercase, without #). This is also the partition key.</summary>
    [JsonPropertyName("hashtag")]
    public string Hashtag { get; set; } = string.Empty;

    /// <summary>Top post references (capped at 100). Most recent first.</summary>
    [JsonPropertyName("topPosts")]
    public List<HashtagPostRef> TopPosts { get; set; } = new();

    /// <summary>Total number of posts that used this hashtag (not capped).</summary>
    [JsonPropertyName("totalPostCount")]
    public long TotalPostCount { get; set; }

    /// <summary>Soft-delete flag. When true the document is logically deleted.</summary>
    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }

    /// <summary>UTC timestamp of when the document was soft-deleted. Null if not deleted.</summary>
    [JsonPropertyName("deletedAt")]
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>
/// A lightweight reference to a post within a <see cref="HashtagDocument"/>.
/// </summary>
public class HashtagPostRef
{
    [JsonPropertyName("postId")]
    public string PostId { get; set; } = string.Empty;

    [JsonPropertyName("imageUrl")]
    public string ImageUrl { get; set; } = string.Empty;
}
