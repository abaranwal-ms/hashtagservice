namespace HashtagService.Shared.Models;

/// <summary>
/// Lightweight event published to posts-topic by PostCreator.
/// Contains only the post ID — the consumer fetches full post data from Cosmos DB.
/// </summary>
public class PostNotification
{
    /// <summary>The ID of the post that was created.</summary>
    public Guid PostId { get; set; }
}

