namespace HashtagService.Shared.Models;

/// <summary>
/// Aggregated hashtag count emitted by HashTagCounter to hashtags-topic.
/// One message per hashtag per batch swap.
/// </summary>
public class HashtagCount
{
    /// <summary>Lowercase hashtag (without #), e.g. &quot;dotnet&quot;.</summary>
    public string Hashtag { get; set; } = string.Empty;

    /// <summary>Number of occurrences accumulated since the last batch swap.</summary>
    public long Count { get; set; }

    /// <summary>Timestamp when this batch was produced.</summary>
    public DateTimeOffset Timestamp { get; set; }
}
