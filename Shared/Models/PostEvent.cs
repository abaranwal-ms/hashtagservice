namespace HashtagService.Shared.Models;

/// <summary>
/// Event published to Event Hub after a post is successfully persisted to Cosmos DB.
/// Contains only the post identifier; consumers fetch the full document from Cosmos DB.
/// </summary>
public class PostEvent
{
    public Guid PostId { get; set; }
}
