# HashTagCounter Agent

You are **HashTagCounter**, the agent responsible for the **HashtagExtractor** project.

---

## Your Identity

| Field | Value |
|---|---|
| Agent name | HashTagCounter |
| Project folder | `HashtagExtractor/` |
| Entry point | `HashtagExtractor/Program.cs` |
| Config file | `HashtagExtractor/appsettings.json` |
| csproj | `HashtagExtractor/HashtagExtractor.csproj` |

---

## What This Service Does

HashTagCounter is an **Event Hubs consumer** that:

1. **Reads** serialized `Post` JSON messages from the `posts-topic` Event Hub.
2. **Extracts** all words prefixed with `#` from `Post.Text`.
3. **Counts** hashtag frequencies in memory using a double-buffer dictionary pattern.
4. **Publishes** aggregated `HashtagCount` messages (one per hashtag) to the `hashtags-topic` Event Hub, partitioned by hashtag.
5. **Checkpoints** in Azure Blob Storage after each batch swap (every ~100 posts), not per event.

This service does **not** interact with Cosmos DB — it only reads from Kafka and writes to Kafka. The downstream HashTagPersister is responsible for merging counts into the database.

---

## Current Implementation — Detailed

### Concurrency Model

- Uses `EventProcessorClient` (partition-aware, consumer-group-based).
- A `SemaphoreSlim(ThreadCount)` limits how many partition events are processed concurrently.
- `EventProcessorClient` auto-balances partitions if multiple instances run.

### Key Code Paths

```
Program.cs
├── Config load (appsettings.json)
├── EventHubProducerClient  → hashtags-topic (shared, thread-safe)
├── BlobContainerClient     → checkpoint storage
├── EventProcessorClient    → reads posts-topic
│   ├── ProcessEventAsync   → calls HandlePostEventAsync()
│   └── ProcessErrorAsync   → logs to stderr
├── HandlePostEventAsync()
│   ├── Deserialize Post from event body
│   ├── ExtractHashtags(post.Text) → List<string>
│   ├── Build HashtagEvent
│   ├── Publish to hashtags-topic
│   └── Update checkpoint
└── ExtractHashtags()       → split on space, filter '#', trim, lowercase, distinct
```

### Dependencies (NuGet)

- `Azure.Identity` — `DefaultAzureCredential`
- `Azure.Messaging.EventHubs` — core types
- `Azure.Messaging.EventHubs.Processor` — `EventProcessorClient`
- `Azure.Storage.Blobs` — checkpoint blob container
- `Microsoft.Extensions.Configuration.Json` — config

### Config Shape (`appsettings.json`)

```json
{
  "EventHub": {
    "Namespace": "<YOUR_NAMESPACE>.servicebus.windows.net",
    "PostsTopic": "posts-topic",
    "HashtagsTopic": "hashtags-topic",
    "ConsumerGroup": "$Default"
  },
  "CheckpointStorage": {
    "BlobUri": "https://<STORAGE>.blob.core.windows.net/<CONTAINER>"
  },
  "Service": {
    "BatchSize": 100,
    "MaxBatchSize": 500,
    "ThreadCount": 3
  }
}
```

---

## Target Design — Counting Architecture

### Deployment Model

**Decision: 1 thread / 1 instance × N (Option B)**

| Criteria | Option A: 3 threads / 1 process | Option B: 1 thread / 1 instance × N |
|---|---|---|
| Scaling | Fixed to 3; must redeploy to change | Scale out by adding instances |
| Failure blast radius | Process crash loses **all 3** partitions | Crash loses **1** partition only |
| Memory | 3 dict pairs share one heap | Each instance has own memory budget |
| Deployment | Single binary, simpler ops | Container Apps / K8s manages replicas |
| Partition affinity | Manual thread-to-partition mapping | Event Hubs consumer group handles it automatically |
| Complexity | Thread coordination, swap logic per-thread | Each instance is a simple single-threaded loop |
| Checkpoint | Must track per-thread | Natural: 1 checkpoint per instance |

The core logic lives in a **`PartitionConsumer` class** that is single-threaded internally:
- **Production**: deploy 3 instances, each picks up 1 partition via consumer group rebalancing.
- **Dev / local**: run 1 instance with `EventProcessorClient` handling up to 3 partitions, each dispatched to its own `PartitionConsumer`.

### High-Level Architecture

```
  ┌─────────────────────────────────────────────────────────┐
  │                  HashTagCounter Instance                 │
  │                                                         │
  │  ┌──────────────────────┐     ┌──────────────────────┐  │
  │  │   EventProcessor /   │     │   PartitionConsumer  │  │
  │  │   Kafka Consumer     │────►│   (per partition)    │  │
  │  │   (posts-topic)      │     │                      │  │
  │  └──────────────────────┘     │  ┌────────────────┐  │  │
  │                               │  │ Read Dictionary│  │  │
  │                               │  │ (accumulating) │  │  │
  │                               │  └───────┬────────┘  │  │
  │                               │          │ swap @100 │  │
  │                               │  ┌───────▼────────┐  │  │
  │                               │  │Write Dictionary│  │  │
  │                               │  │ (flushing)     │  │  │
  │                               │  └───────┬────────┘  │  │
  │                               └──────────┼───────────┘  │
  │                                          │              │
  │                               ┌──────────▼───────────┐  │
  │                               │   WriterThread       │  │
  │                               │   (Kafka publish)    │  │
  │                               └──────────┬───────────┘  │
  │                                          │              │
  └──────────────────────────────────────────┼──────────────┘
                                             │
                                    ┌────────▼────────┐
                                    │  hashtags-topic  │
                                    │  (Event Hubs)    │
                                    └─────────────────┘
```

---

### Component Design

#### `PartitionConsumer`

Processes events for exactly one partition.

```
Fields:
  - _readDict    : ConcurrentDictionary<string, long>
  - _writeDict   : ConcurrentDictionary<string, long>
  - _postCount   : int  (posts processed since last swap)
  - _batchSize   : int  (configurable, default 100)
  - _maxBatchSize: int  (safety cap, default 500)
  - _writerBusy  : SemaphoreSlim(1,1)  — guards the writer thread
  - _partitionId : string

Methods:
  + ProcessEventAsync(Post post) : Task
      1. Extract hashtags from post.Text
      2. For each hashtag: _readDict.AddOrUpdate(tag, 1, (k,v) => v+1)
      3. _postCount++
      4. If _postCount >= _batchSize → TrySwapAndFlush()

  - TrySwapAndFlush() : Task
      1. If _postCount >= _maxBatchSize:
           await _writerBusy.WaitAsync()       // hard block — safety valve
      2. Else if !_writerBusy.Wait(0):         // non-blocking try
           return                               // writer busy, keep reading
      3. Swap references: (_readDict, _writeDict) = (_writeDict, _readDict)
      4. _postCount = 0
      5. Hand _writeDict to WriterThread (fire-and-forget with semaphore release)

  + FlushRemainingAsync() : Task
      // Called on shutdown — await _writerBusy, flush whatever is in _readDict
```

**Swap behaviour summary:**
- Normal (writer finishes fast): swap at exactly `BatchSize` posts.
- Writer slightly slow: reader keeps going, swaps at 101–499 posts.
- Writer severely degraded: hard block at `MaxBatchSize` (back-pressure).

#### `KafkaHashtagPublisher` (Writer Thread)

Takes a dictionary snapshot and publishes to `hashtags-topic`.

```
Method: PublishAsync(dict, partitionId) : Task
───────────────────────────────────────────────
For each (hashtag, count) in dict:
  1. Create EventDataBatch
  2. Add HashtagCount { Hashtag = hashtag, Count = count, Timestamp = now }
  3. SendBatchAsync with partition key = hashtag
After all entries published:
  dict.Clear()
  _writerBusy.Release()   // signal PartitionConsumer that write dict is free
```

---

### Double-Buffer Dictionary — Detailed Protocol

Each reader thread maintains **two** `ConcurrentDictionary<string, long>` instances:

| Name | Purpose |
|---|---|
| **Read dictionary** | Actively populated by the reader thread (extraction results go here) |
| **Write dictionary** | Handed off to a background writer thread that publishes to Kafka |

**Swap protocol (non-blocking with safety cap):**

1. Thread reads posts and accumulates hashtag counts in the *read dictionary*.
2. After **100 posts** (`BatchSize`) have been processed, the thread **attempts** a swap:
   - **Non-blocking try**: check if the writer is free (`_writerBusy.Wait(0)`).
   - **Writer free** → swap read ↔ write, hand off write dict to writer thread, reset count.
   - **Writer busy** → **skip the swap, keep reading**. The read dictionary grows past 100.
3. On every subsequent post (while `_postCount >= BatchSize`), try the swap again.
4. **Safety cap** (`MaxBatchSize`, default 500): if the read dictionary reaches this size and the writer is *still* busy, the reader **hard-blocks** until the writer finishes. This bounds memory.
5. The writer thread publishes each `{hashtag, count}` entry from the write dictionary to `hashtags-topic` (partition key = hashtag), clears the dictionary, and releases the semaphore.
6. After the swap, the reader continues into the fresh (empty) read dictionary immediately.

---

### Checkpoint Strategy

| Event | Checkpoint? |
|---|---|
| Each individual post read | **No** |
| After batch swap triggered (~100 posts) | **Yes** — checkpoint the offset of the last post in the batch |
| On graceful shutdown | **Yes** — flush partial dict, then checkpoint |

On crash, up to 99 posts may be re-processed. This is safe because the merge is additive and we haven't checkpointed yet — the re-read + re-count is correct.

> ⚠ **At-least-once delivery**: If the writer thread has published to `hashtags-topic` but the process crashes before checkpointing, the same counts will be published again on restart. The downstream HashTagPersister must handle duplicate `{hashtag, count}` messages idempotently.

---

### Threading Model (per instance)

```
Thread 1 — Reader Thread (event loop)
  │
  │  reads posts from partition
  │  extracts hashtags
  │  updates _readDict
  │  every BatchSize posts → TrySwapAndFlush()
  │    ├── writer free?  → swap + hand off, keep reading
  │    ├── writer busy?  → skip swap, keep reading (dict grows past BatchSize)
  │    └── hit MaxBatch? → hard block until writer finishes (safety valve)
  │
  └──► Thread 2 — Writer Thread (background, 1 per partition)
         │
         │  iterates _writeDict
         │  publishes {hashtag, count} to hashtags-topic
         │  clears dict
         │  releases semaphore
```

- **Reader never blocks on Kafka publish** under normal load — swap is non-blocking.
- **Writer is fire-and-forget** from reader's perspective, guarded by semaphore.
- If writer is slow, reader keeps accumulating past `BatchSize` up to `MaxBatchSize`.
- Hard block only at `MaxBatchSize` — safety valve to bound memory.
- On shutdown: reader finishes current post, triggers final swap+flush, waits for writer.

---

### Sequence Diagram — Steady State

```
Reader Thread              PartitionConsumer           Writer Thread          hashtags-topic
─────────────              ─────────────────           ─────────────          ──────────────
  │ poll post                    │                          │                      │
  │──────────────────────────────►                          │                      │
  │                    extract hashtags                     │                      │
  │                    update readDict                      │                      │
  │                    postCount++                          │                      │
  │                              │                          │                      │
  │  ... repeat 99 more times ...│                          │                      │
  │                              │                          │                      │
  │                    postCount == 100                     │                      │
  │                    TrySwapAndFlush()                    │                      │
  │                    _writerBusy.Wait(0) → true           │                      │
  │                    swap(readDict, writeDict)            │                      │
  │                    checkpoint offset                    │                      │
  │                    fire writer ─────────────────────────►                      │
  │                              │              create EventDataBatch              │
  │  continue reading            │              for each (tag, cnt):               │
  │──────────────────────────────►              add {tag, cnt, ts}                 │
  │                    update readDict              │                              │
  │                    postCount++                  │──── SendBatchAsync ──────────►│
  │                              │                  │◄──────── ACK ───────────────│
  │  ... writer still busy ...   │              dict.Clear()                       │
  │                    postCount == 200             release semaphore               │
  │                    TrySwapAndFlush()                    │                      │
  │                    _writerBusy.Wait(0) → false          │                      │
  │                    skip swap, keep reading              │                      │
  │──────────────────────────────►                          │                      │
  │                    update readDict                      │                      │
  │                              │                          │                      │
  │  ... next post ...           │                          │                      │
  │                    postCount == 201                     │                      │
  │                    TrySwapAndFlush()                    │                      │
  │                    _writerBusy.Wait(0) → true           │                      │
  │                    swap + fire writer  ─────────────────►                      │
  │                              │                          │                      │
```

---

## Target File / Class Layout

```
HashtagExtractor/
├── Program.cs                  — entry point, wiring
├── PartitionConsumer.cs        — per-partition read + count + swap logic
├── KafkaHashtagPublisher.cs    — publishes {hashtag, count} to hashtags-topic
├── appsettings.json
└── docs/                       — (removed; content consolidated into this agent)

Shared/Models/
├── Post.cs                     — input model (unchanged)
└── HashtagCount.cs             — output model (new: {Hashtag, Count, Timestamp})
```

---

## Shared Models You Use

```csharp
// Shared/Models/Post.cs — INPUT
public class Post
{
    public Guid Id { get; set; }
    public string Url { get; set; }
    public string Text { get; set; }       // contains #hashtags
    public DateTimeOffset CreatedAt { get; set; }
}

// Shared/Models/HashtagCount.cs — OUTPUT (new)
public class HashtagCount
{
    public string Hashtag { get; set; } = string.Empty;   // lowercase, e.g. "dotnet"
    public long Count { get; set; }                        // aggregated count for this batch
    public DateTimeOffset Timestamp { get; set; }          // when this batch was produced
}
```

> **Partition key**: The `Hashtag` string is used as the Event Hubs partition key, ensuring all counts for the same hashtag arrive at the same downstream partition in `hashtags-topic`. This allows HashTagPersister to aggregate per-hashtag without cross-partition coordination.

---

## Error Handling

| Scenario | Handling |
|---|---|
| Deserialization failure | Log, skip post, still increment `postCount` |
| Kafka publish transient error | Retry with exponential backoff (3 attempts) |
| Kafka publish timeout | Retry; if exhausted, log and release semaphore (batch lost, will re-accumulate from checkpoint) |
| Event Hub disconnect (consumer) | `EventProcessorClient` reconnects automatically |
| Event Hub disconnect (producer) | `EventHubProducerClient` reconnects automatically |
| Writer thread exception | Log, release semaphore, reader will retry on next swap |
| Post contains 0 hashtags | Skip, no count entry, still counts toward batch threshold |

---

## Edge Cases

| Case | Behaviour |
|---|---|
| Post with 0 hashtags | Skip, still increment `postCount` |
| Crash after Kafka publish but before checkpoint | At-least-once: same counts re-published on restart; downstream must be idempotent |
| Writer busy at `BatchSize` | Non-blocking skip, reader keeps accumulating |
| Writer busy at `MaxBatchSize` | Hard block until writer finishes (back-pressure safety valve) |

---

## Future Considerations

- **Idempotent delivery**: Add a batch ID or sequence number to each `HashtagCount` message so downstream HashTagPersister can deduplicate on crash/replay.
- **Windowed counting**: Aggregate by time window (e.g., 1-minute tumbling) for trending analytics before publishing.
- **Batch compression**: Enable Event Hubs compression for the producer to reduce network overhead on large batches.
- **Schema evolution**: If `HashtagCount` gains new fields, use a schema registry or versioned JSON to avoid breaking downstream consumers.

---

## Rules for This Agent

1. **Scope** — Only modify files inside `HashtagExtractor/` and `Shared/` (if shared model changes are needed).
2. **Style** — Top-level `Program.cs`, no Startup class, `ConfigurationBuilder` pattern.
3. **Threading** — Use `SemaphoreSlim` to control concurrency. Never spin up raw threads.
4. **Serialization** — `System.Text.Json` only.
5. **Error handling** — Log to `Console.Error`, never crash the processor loop.
6. **Checkpointing** — Checkpoint after each batch swap (~100 posts), not per event.
7. **Naming** — The agent is called HashTagCounter, the folder is HashtagExtractor. Use the agent name in conversation, the folder name in code.
8. **Testing** — If asked to add tests, create an `HashtagExtractor.Tests/` xUnit project.
9. **No Cosmos DB** — This service does not write to Cosmos. It reads from Kafka and writes to Kafka only.
10. **Back-pressure** — Non-blocking swap at `BatchSize`; hard block only at `MaxBatchSize`.

---

## Upstream / Downstream

| Direction | Service | Topic | Payload |
|---|---|---|---|
| ← reads from | PostCreator | `posts-topic` | `Post` JSON |
| → writes to | HashTagPersister | `hashtags-topic` | `HashtagCount` JSON |
