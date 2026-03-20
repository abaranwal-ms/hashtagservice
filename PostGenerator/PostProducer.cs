using Bogus;
using HashtagService.Shared.Handlers;
using HashtagService.Shared.Models;

namespace PostGenerator;

/// <summary>
/// Encapsulates the post-generation + publish loop so it can be tested
/// with mocked Cosmos and Event Hub dependencies.
/// </summary>
public class PostProducer
{
    private readonly IPostDocumentHandler _postHandler;
    private readonly IEventHubProducerHandler _eventHubHandler;

    public PostProducer(IPostDocumentHandler postHandler, IEventHubProducerHandler eventHubHandler)
    {
        _postHandler = postHandler ?? throw new ArgumentNullException(nameof(postHandler));
        _eventHubHandler = eventHubHandler ?? throw new ArgumentNullException(nameof(eventHubHandler));
    }

    /// <summary>
    /// Creates a Bogus Faker configured to generate realistic Posts.
    /// A new instance should be created per thread (Faker is not thread-safe).
    /// </summary>
    public static Faker<Post> CreateFaker() => new Faker<Post>()
        .RuleFor(p => p.Id, f => f.Random.Guid())
        .RuleFor(p => p.UserId, f => f.Internet.UserName())
        .RuleFor(p => p.Url, f => $"https://{f.Internet.DomainName()}/content/{f.Random.AlphaNumeric(12)}")
        .RuleFor(p => p.Text, f =>
        {
            var words = f.Lorem.Words(f.Random.Number(7, 11)).ToList();
            int hashtagCount = f.Random.Number(1, 3);
            var indices = Enumerable.Range(0, words.Count).OrderBy(_ => f.Random.Double()).Take(hashtagCount);
            foreach (var idx in indices)
            {
                words[idx] = $"#{f.Hacker.Noun().Replace(" ", "")}";
            }
            return string.Join(" ", words);
        })
        .RuleFor(p => p.CreatedAt, f => f.Date.RecentOffset(days: 1));

    /// <summary>
    /// Produces a single batch of posts: persists each to Cosmos DB, then
    /// emits a <see cref="PostEvent"/> to Event Hubs.
    /// Returns the list of posts that were created.
    /// </summary>
    public async Task<List<Post>> ProduceBatchAsync(Faker<Post> faker, int batchSize, CancellationToken ct = default)
    {
        var posts = new List<Post>(batchSize);

        for (int i = 0; i < batchSize; i++)
        {
            ct.ThrowIfCancellationRequested();

            var post = faker.Generate();

            // 1. Persist the full post document to Cosmos DB
            await _postHandler.InsertAsync(PostDocument.FromPost(post), ct);

            // 2. Emit a PostEvent (post ID only) to Event Hub
            var postEvent = new PostEvent { PostId = post.Id };
            await _eventHubHandler.SendEventAsync(postEvent, post.Id.ToString(), ct);

            posts.Add(post);
        }

        return posts;
    }

    /// <summary>
    /// Continuous producer loop: generates batches at the configured interval
    /// until cancellation is requested.
    /// </summary>
    public async Task RunAsync(int threadId, int batchSize, int intervalMs, CancellationToken ct)
    {
        var faker = CreateFaker();
        long total = 0;

        while (!ct.IsCancellationRequested)
        {
            var posts = await ProduceBatchAsync(faker, batchSize, ct);
            total += posts.Count;
            Console.WriteLine($"[Thread-{threadId:D2}] Sent batch of {posts.Count} posts (total: {total})");

            await Task.Delay(intervalMs, ct);
        }
    }
}
