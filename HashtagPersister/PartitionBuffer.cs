using System.Collections.Concurrent;
using HashtagService.Shared.Handlers;
using HashtagService.Shared.Models;
using Microsoft.Azure.Cosmos;

namespace HashtagPersister;

/// <summary>
/// Double-buffer accumulator for a single Event Hub partition.
/// The reader accumulates hashtag counts in the read dictionary.
/// At batch boundaries the buffers swap and a background writer
/// flushes the write dictionary to Cosmos DB concurrently.
/// </summary>
public sealed class PartitionBuffer
{
    private ConcurrentDictionary<string, long> _readDict  = new();
    private ConcurrentDictionary<string, long> _writeDict = new();

    private readonly SemaphoreSlim _writerBusy = new(1, 1);
    private int _eventCount;
    private long _totalProcessed;

    private readonly string _partitionId;
    private readonly HashtagDocumentHandler _handler;
    private readonly int _batchSize;
    private readonly int _maxBatchSize;
    private readonly int _maxConcurrency;

    public PartitionBuffer(
        string partitionId,
        HashtagDocumentHandler handler,
        int batchSize,
        int maxBatchSize,
        int maxConcurrency)
    {
        _partitionId    = partitionId;
        _handler        = handler;
        _batchSize      = batchSize;
        _maxBatchSize   = maxBatchSize;
        _maxConcurrency = maxConcurrency;
    }

    public long TotalProcessed => _totalProcessed;

    /// <summary>
    /// Accumulates a hashtag count into the read buffer.
    /// When the buffer reaches <c>BatchSize</c>, attempts a non-blocking swap + background flush.
    /// At <c>MaxBatchSize</c>, hard-blocks until the writer finishes (back-pressure).
    /// </summary>
    /// <param name="hashtag">Lowercase hashtag string.</param>
    /// <param name="count">Aggregated count from the upstream batch.</param>
    /// <param name="checkpointFunc">
    /// Closure over <c>args.UpdateCheckpointAsync</c> — called after the flush completes,
    /// so the checkpoint reflects only persisted data.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task AccumulateAsync(
        string hashtag, long count,
        Func<CancellationToken, Task> checkpointFunc,
        CancellationToken ct)
    {
        _readDict.AddOrUpdate(hashtag, count, (_, existing) => existing + count);
        _totalProcessed++;
        _eventCount++;

        if (_eventCount >= _batchSize)
        {
            await TrySwapAndFlushAsync(checkpointFunc, ct);
        }
    }

    /// <summary>
    /// Called on graceful shutdown — waits for any in-progress writer,
    /// then flushes whatever remains in the read dictionary.
    /// </summary>
    public async Task FlushRemainingAsync(CancellationToken ct)
    {
        // Wait for any in-progress background flush to finish
        await _writerBusy.WaitAsync(ct);
        try
        {
            if (_readDict.IsEmpty) return;

            Console.WriteLine(
                $"[Partition-{_partitionId}] Final flush of {_readDict.Count} remaining hashtag(s)...");
            await FlushDictAsync(_readDict, ct);
            _readDict.Clear();
            _eventCount = 0;

            Console.WriteLine(
                $"[Partition-{_partitionId}] Final flush complete — total events processed: {_totalProcessed}");
        }
        finally
        {
            _writerBusy.Release();
        }
    }

    // ── private ─────────────────────────────────────────────────────

    private async Task TrySwapAndFlushAsync(
        Func<CancellationToken, Task> checkpointFunc,
        CancellationToken ct)
    {
        if (_eventCount >= _maxBatchSize)
        {
            // Safety valve: hard-block until the writer finishes to bound memory
            await _writerBusy.WaitAsync(ct);
        }
        else if (!_writerBusy.Wait(0))
        {
            // Writer still busy — keep accumulating in the read dict
            return;
        }

        // Swap: reader gets the empty dict, writer gets the full one
        (_readDict, _writeDict) = (_writeDict, _readDict);
        var eventsInBatch = _eventCount;
        _eventCount = 0;

        // Fire background writer (fire-and-forget; semaphore release guarantees ordering)
        var dictToFlush = _writeDict;
        _ = Task.Run(async () =>
        {
            try
            {
                await FlushDictAsync(dictToFlush, ct);
                await checkpointFunc(ct);
                Console.WriteLine(
                    $"[Partition-{_partitionId}] Flushed {dictToFlush.Count} hashtag(s) " +
                    $"({eventsInBatch} events) — total: {_totalProcessed}");
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[Partition-{_partitionId}] Background flush error: {ex.Message}");
            }
            finally
            {
                dictToFlush.Clear();
                _writerBusy.Release();
            }
        }, ct);
    }

    private async Task FlushDictAsync(
        ConcurrentDictionary<string, long> dict, CancellationToken ct)
    {
        await Parallel.ForEachAsync(dict, new ParallelOptions
        {
            MaxDegreeOfParallelism = _maxConcurrency,
            CancellationToken = ct
        }, async (entry, innerCt) =>
        {
            var (hashtag, count) = entry;
            try
            {
                var doc = await _handler.GetAsync(hashtag, innerCt)
                          ?? new HashtagDocument { Hashtag = hashtag };

                doc.TotalPostCount += count;
                await _handler.UpdateAsync(doc, innerCt);
            }
            catch (OperationCanceledException) { throw; }
            catch (CosmosException ex)
            {
                Console.Error.WriteLine(
                    $"[HashtagPersister] Cosmos error '{hashtag}' (+{count}): " +
                    $"{ex.StatusCode} — {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[HashtagPersister] Error '{hashtag}' (+{count}): {ex.Message}");
            }
        });
    }
}