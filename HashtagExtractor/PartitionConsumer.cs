using System.Collections.Concurrent;
using Azure.Messaging.EventHubs.Processor;
using HashtagService.Shared.Handlers;
using HashtagService.Shared.Models;

namespace HashtagExtractor;

/// <summary>
/// Per-partition consumer that fetches posts from Cosmos DB, extracts hashtags,
/// accumulates counts in a double-buffer dictionary, and hands off to
/// <see cref="KafkaHashtagPublisher"/> for background publishing.
/// </summary>
public class PartitionConsumer
{
    private ConcurrentDictionary<string, long> _readDict = new();
    private ConcurrentDictionary<string, long> _writeDict = new();
    private int _postCount;
    private readonly int _batchSize;
    private readonly int _maxBatchSize;
    private readonly SemaphoreSlim _writerBusy = new(1, 1);
    private readonly string _partitionId;
    private readonly KafkaHashtagPublisher _publisher;
    private readonly PostDocumentHandler _postHandler;
    private ProcessEventArgs? _latestArgs;

    public PartitionConsumer(string partitionId, KafkaHashtagPublisher publisher, PostDocumentHandler postHandler, int batchSize, int maxBatchSize)
    {
        _partitionId = partitionId;
        _publisher = publisher;
        _postHandler = postHandler;
        _batchSize = batchSize;
        _maxBatchSize = maxBatchSize;
    }

    /// <summary>
    /// Processes a post notification: fetches the post from Cosmos DB by ID,
    /// extracts hashtags, accumulates counts, and triggers a swap+flush
    /// when the batch threshold is reached.
    /// </summary>
    public async Task ProcessEventAsync(Guid postId, ProcessEventArgs args, CancellationToken ct)
    {
        _latestArgs = args;

        // Fetch the full post from Cosmos DB
        var postDoc = await _postHandler.GetAsync(postId.ToString(), ct);
        if (postDoc is null)
        {
            Console.Error.WriteLine($"[Partition-{_partitionId}] Post {postId} not found in Cosmos DB, skipping");
            _postCount++;
            if (_postCount >= _batchSize) await TrySwapAndFlushAsync(ct);
            return;
        }

        var hashtags = ExtractHashtags(postDoc.Text);

        if (hashtags.Count > 0)
        {
            foreach (var tag in hashtags)
            {
                _readDict.AddOrUpdate(tag, 1, (_, v) => v + 1);
            }
        }

        _postCount++;

        if (_postCount >= _batchSize)
        {
            await TrySwapAndFlushAsync(ct);
        }
    }

    /// <summary>
    /// Attempts to swap the read/write dictionaries and flush the write dictionary
    /// to hashtags-topic. Non-blocking at BatchSize, hard-blocks at MaxBatchSize.
    /// </summary>
    private async Task TrySwapAndFlushAsync(CancellationToken ct)
    {
        if (_postCount >= _maxBatchSize)
        {
            // Safety valve: hard block until writer finishes
            Console.WriteLine($"[Partition-{_partitionId}] Hit MaxBatchSize ({_maxBatchSize}), blocking until writer finishes...");
            await _writerBusy.WaitAsync(ct);
        }
        else if (!_writerBusy.Wait(0))
        {
            // Writer still busy, skip swap and keep reading
            return;
        }

        // Semaphore acquired — swap dictionaries
        var swappedCount = _postCount;
        var uniqueTags = _readDict.Count;
        (_readDict, _writeDict) = (_writeDict, _readDict);
        _postCount = 0;

        Console.WriteLine(
            $"[Partition-{_partitionId}] Swap: {swappedCount} posts, {uniqueTags} unique hashtag(s) -> flushing");

        // Capture references for the background writer
        var dictToFlush = _writeDict;
        var argsToCheckpoint = _latestArgs;
        var pid = _partitionId;

        _ = Task.Run(async () =>
        {
            try
            {
                await _publisher.PublishAsync(dictToFlush, pid, ct);

                // Checkpoint after successful publish
                if (argsToCheckpoint is { } checkpoint)
                {
                    await checkpoint.UpdateCheckpointAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[Partition-{pid}] Writer error: {ex.Message}");
            }
            finally
            {
                _writerBusy.Release();
            }
        }, ct);
    }

    /// <summary>
    /// Called on graceful shutdown. Waits for any in-flight writer to finish,
    /// then flushes whatever remains in the read dictionary.
    /// </summary>
    public async Task FlushRemainingAsync(CancellationToken ct)
    {
        await _writerBusy.WaitAsync(ct);
        try
        {
            if (!_readDict.IsEmpty)
            {
                Console.WriteLine(
                    $"[Partition-{_partitionId}] Shutdown flush: {_readDict.Count} unique hashtag(s) from {_postCount} remaining post(s)");

                await _publisher.PublishAsync(_readDict, _partitionId, ct);

                if (_latestArgs is { } checkpoint)
                {
                    await checkpoint.UpdateCheckpointAsync(ct);
                }
            }
        }
        finally
        {
            _writerBusy.Release();
        }
    }

    /// <summary>
    /// Extracts hashtag words from post text. Returns lowercase, distinct, without '#'.
    /// </summary>
    internal static List<string> ExtractHashtags(string text)
    {
        return text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.StartsWith('#') && w.Length > 1)
            .Select(w => w.TrimStart('#').ToLowerInvariant())
            .Distinct()
            .ToList();
    }
}

