namespace HashtagService.Shared.Models;

public class HashtagEvent
{
    public Guid PostId { get; set; }
    public List<string> Hashtags { get; set; } = new();
    public DateTimeOffset ExtractedAt { get; set; }
}
