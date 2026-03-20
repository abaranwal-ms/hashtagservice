namespace HashtagService.Shared.Models;

public class Post
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Synthetic partition key for the Posts Cosmos DB container.
    // Format: "{UserId}_{YYYYMM}" (e.g. "user42_202603").
    // Distributes writes across users while keeping one user's monthly
    // posts on the same logical partition for efficient per-user queries.
    public string PartitionKey =>
        $"{UserId}_{CreatedAt:yyyyMM}";

    // TODO: Reverse-index container ("UserPartitions").
    // For every new {UserId}_{YYYYMM} bucket that is written for the first
    // time, upsert a document keyed by UserId that tracks the list (or count)
    // of active monthly partitions for that user.
    // Suggested document shape (one document per userId in this container):
    //   { "id": "<userId>",    -- unique id within the partition
    //     "userId": "<userId>",-- required: Cosmos partition key field (/userId)
    //     "partitions": ["202601", "202602", "202603"], "partitionCount": 3 }
    // Partition key of this container: /userId
    // This allows a single-partition read to discover all months a user has
    // data in, avoiding a full cross-partition scan when querying historical
    // posts for a given user.
}
