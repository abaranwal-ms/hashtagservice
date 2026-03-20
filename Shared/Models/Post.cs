namespace HashtagService.Shared.Models;

public class Post
{
    public Guid Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
