using System.Collections.Concurrent;
using HashtagService.Shared.Handlers;
using HashtagService.Shared.Models;

namespace HashtagExtractor;

/// <summary>
/// Writer thread: takes a dictionary of accumulated hashtag counts and publishes
/// each entry as a <see cref="HashtagCount"/> message to hashtags-topic.
/// Each hashtag uses its own partition key for downstream affinity.
/// Delegates to the shared <see cref="EventHubProducerHandler"/> for serialization and sending.
/// </summary>
public class KafkaHashtagPublisher
{
    private readonly EventHubProducerHandler _producerHandler;

    public KafkaHashtagPublisher(EventHubProducerHandler producerHandler)
    {
        _producerHandler = producerHandler ?? throw new ArgumentNullException(nameof(producerHandler));
    }

    /// <summary>
    /// Publishes all entries from <paramref name="dict"/> to hashtags-topic,
    /// then clears the dictionary.
    /// </summary>
    public async Task PublishAsync(ConcurrentDictionary<string, long> dict, string partitionId, CancellationToken ct)
    {
        if (dict.IsEmpty) return;

        var timestamp = DateTimeOffset.UtcNow;
        int published = 0;

        foreach (var (hashtag, count) in dict)
        {
            var hashtagCount = new HashtagCount
            {
                Hashtag = hashtag,
                Count = count,
                Timestamp = timestamp
            };

            await SendWithRetryAsync(hashtagCount, hashtag, ct);
            published++;
        }

        Console.WriteLine($"[Partition-{partitionId}] Published {published} hashtag count(s) to hashtags-topic");
        dict.Clear();
    }

    private async Task SendWithRetryAsync(HashtagCount payload, string partitionKey, CancellationToken ct, int maxRetries = 3)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await _producerHandler.SendEventAsync(payload, partitionKey, ct);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (attempt < maxRetries)
            {
                var delay = TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt));
                Console.Error.WriteLine(
                    $"[KafkaHashtagPublisher] Send attempt {attempt}/{maxRetries} failed: {ex.Message}. Retrying in {delay.TotalMilliseconds}ms...");
                await Task.Delay(delay, ct);
            }
        }
    }
}
