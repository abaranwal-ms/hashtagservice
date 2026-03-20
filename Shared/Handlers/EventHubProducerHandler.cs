using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HashtagService.Shared.Handlers;

/// <summary>
/// Interface for sending events to an Event Hub.
/// </summary>
public interface IEventHubProducerHandler
{
    Task SendEventAsync<T>(T payload, string? partitionKey = null, CancellationToken cancellationToken = default);
    Task SendEventsBatchAsync<T>(IEnumerable<T> payloads, string? partitionKey = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Simple handler for sending events to an Event Hub.
/// </summary>
public class EventHubProducerHandler : IEventHubProducerHandler
{
    private readonly EventHubProducerClient _producerClient;

    public EventHubProducerHandler(EventHubProducerClient producerClient)
    {
        _producerClient = producerClient ?? throw new ArgumentNullException(nameof(producerClient));
    }

    /// <summary>
    /// Serializes an object to JSON and sends it.
    /// </summary>
    public async Task SendEventAsync<T>(T payload, string? partitionKey = null, CancellationToken cancellationToken = default)
    {
        var options = new CreateBatchOptions();
        if (!string.IsNullOrEmpty(partitionKey))
        {
            options.PartitionKey = partitionKey;
        }

        using var batch = await _producerClient.CreateBatchAsync(options, cancellationToken);
        var eventData = new EventData(JsonSerializer.SerializeToUtf8Bytes(payload));
        
        if (!batch.TryAdd(eventData))
        {
            throw new Exception("Event is too large to fit in a single batch.");
        }

        await _producerClient.SendAsync(batch, cancellationToken);
    }

    /// <summary>
    /// Serializes multiple objects to JSON and sends them in a single batch.
    /// Throws if they cannot all fit into one batch.
    /// </summary>
    public async Task SendEventsBatchAsync<T>(IEnumerable<T> payloads, string? partitionKey = null, CancellationToken cancellationToken = default)
    {
        var options = new CreateBatchOptions();
        if (!string.IsNullOrEmpty(partitionKey))
        {
            options.PartitionKey = partitionKey;
        }

        using var batch = await _producerClient.CreateBatchAsync(options, cancellationToken);
        foreach (var payload in payloads)
        {
            var eventData = new EventData(JsonSerializer.SerializeToUtf8Bytes(payload));
            if (!batch.TryAdd(eventData))
            {
                throw new Exception("Events exceed the size of a single batch. Paging is not implemented.");
            }
        }

        if (batch.Count > 0)
        {
            await _producerClient.SendAsync(batch, cancellationToken);
        }
    }
}
