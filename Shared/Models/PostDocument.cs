using System.Text.Json.Serialization;

namespace HashtagService.Shared.Models;

/// <summary>
/// Cosmos DB document for the <c>Posts</c> container.
/// Partition key: /id (post GUID). Enables 1 RU point reads by post ID.
/// </summary>
public class PostDocument
{
    /// <summary>Cosmos DB document id and partition key (post GUID as string).</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Soft-delete flag. When true the document is logically deleted.</summary>
    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }

    /// <summary>UTC timestamp of when the document was soft-deleted. Null if not deleted.</summary>
    [JsonPropertyName("deletedAt")]
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>
    /// Creates a <see cref="PostDocument"/> from a <see cref="Post"/> event model.
    /// </summary>
    public static PostDocument FromPost(Post post) => new()
    {
        Id        = post.Id.ToString(),
        UserId    = post.UserId,
        Url       = post.Url,
        Text      = post.Text,
        CreatedAt = post.CreatedAt
    };
}
