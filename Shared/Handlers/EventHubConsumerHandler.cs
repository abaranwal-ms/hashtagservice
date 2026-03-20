using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HashtagService.Shared.Handlers;

/// <summary>
/// Simple handler for consuming events from an Event Hub.
/// Useful for tests or simple tools.
/// </summary>
public class EventHubConsumerHandler
{
    private readonly EventHubConsumerClient _consumerClient;

    public EventHubConsumerHandler(EventHubConsumerClient consumerClient)
    {
        _consumerClient = consumerClient ?? throw new ArgumentNullException(nameof(consumerClient));
    }

    /// <summary>
    /// Reads events from all partitions.
    /// </summary>
    public async IAsyncEnumerable<T> ReadEventsAsync<T>(
        bool startReadingAtEarliestEvent = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var options = new ReadEventOptions { MaximumWaitTime = TimeSpan.FromSeconds(5) };
        await foreach (PartitionEvent partitionEvent in _consumerClient.ReadEventsAsync(startReadingAtEarliestEvent, options, cancellationToken))
        {
            if (partitionEvent.Data != null)
            {
                var payload = JsonSerializer.Deserialize<T>(partitionEvent.Data.EventBody.ToStream());
                if (payload != null)
                {
                    yield return payload;
                }
            }
        }
    }

    /// <summary>
    /// Captures the current tail position of every partition so a subsequent
    /// <see cref="ReadEventsFromPositionAsync{T}"/> call can start right after
    /// the captured point — no race condition with sends.
    /// </summary>
    public async Task<Dictionary<string, EventPosition>> GetLatestPartitionPositionsAsync(CancellationToken cancellationToken = default)
    {
        var positions = new Dictionary<string, EventPosition>();
        var partitionIds = await _consumerClient.GetPartitionIdsAsync(cancellationToken);
        foreach (var pid in partitionIds)
        {
            var props = await _consumerClient.GetPartitionPropertiesAsync(pid, cancellationToken);
            // Start reading from the event AFTER the current last one
            positions[pid] = props.IsEmpty
                ? EventPosition.Earliest
                : EventPosition.FromSequenceNumber(props.LastEnqueuedSequenceNumber, isInclusive: false);
        }
        return positions;
    }

    /// <summary>
    /// Reads events from all partitions starting at the given per-partition positions.
    /// Yields deserialized payloads as they arrive.
    /// </summary>
    public async IAsyncEnumerable<T> ReadEventsFromPositionAsync<T>(
        Dictionary<string, EventPosition> startPositions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var options = new ReadEventOptions { MaximumWaitTime = TimeSpan.FromSeconds(5) };

        // Read each partition in a parallel task and funnel results through a channel-like pattern
        var results = new System.Collections.Concurrent.ConcurrentQueue<T>();
        var tasks = new List<Task>();

        foreach (var kvp in startPositions)
        {
            var partitionId = kvp.Key;
            var position = kvp.Value;

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await foreach (var partitionEvent in _consumerClient.ReadEventsFromPartitionAsync(
                        partitionId, position, options, cancellationToken))
                    {
                        if (partitionEvent.Data != null)
                        {
                            var payload = JsonSerializer.Deserialize<T>(partitionEvent.Data.EventBody.ToStream());
                            if (payload != null)
                            {
                                results.Enqueue(payload);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }, cancellationToken));
        }

        // Yield results as they appear, until cancellation
        while (!cancellationToken.IsCancellationRequested)
        {
            if (results.TryDequeue(out var item))
            {
                yield return item;
            }
            else
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }
}
