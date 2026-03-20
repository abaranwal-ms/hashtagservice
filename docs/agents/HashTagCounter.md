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

HashTagCounter is an **Event Hubs consumer + Cosmos DB reader** that:

1. **Reads** `PostNotification` messages (containing only a `PostId`) from the `posts-topic` Event Hub.
2. **Fetches** the full `PostDocument` from **Azure Cosmos DB** via `PostDocumentHandler.GetAsync()` (1 RU point read).
3. **Extracts** all words prefixed with `#` from `PostDocument.Text`.
4. **Counts** hashtag frequencies in memory using a double-buffer dictionary pattern.
5. **Publishes** aggregated `HashtagCount` messages (one per hashtag) to the `hashtags-topic` Event Hub, partitioned by hashtag.
6. **Checkpoints** in Azure Blob Storage after each batch swap (every ~100 posts), not per event.

This service **reads** from Cosmos DB (Posts container) but does **not write** to it. The downstream HashTagPersister is responsible for merging counts into the Hashtags container.

---

## Current Implementation — Detailed

### Concurrency Model

- Uses `EventProcessorClient` (partition-aware, consumer-group-based).
- One `PartitionConsumer` per partition, created lazily via `ConcurrentDictionary.GetOrAdd()`.
- Each `PartitionConsumer` is single-threaded internally with a background writer thread guarded by `SemaphoreSlim(1,1)`.
- `EventProcessorClient` auto-balances partitions if multiple instances run.

### Key Code Paths

```
Program.cs
├── Config load (appsettings.json)
├── CosmosClient (shared, thread-safe) → PostDocumentHandler
├── EventHubProducerClient → EventHubProducerHandler → KafkaHashtagPublisher
├── BlobContainerClient     → checkpoint storage
├── EventProcessorClient    → reads posts-topic
│   ├── ProcessEventAsync   → deserialize PostNotification → dispatch to PartitionConsumer
│   └── ProcessErrorAsync   → logs to stderr
├── Graceful shutdown → StopProcessingAsync → FlushRemainingAsync per consumer
│
PartitionConsumer.cs (per partition)
├── ProcessEventAsync(postId)
│   ├── PostDocumentHandler.GetAsync(postId) → fetch from Cosmos (1 RU)
│   ├── ExtractHashtags(postDoc.Text) → List<string>
│   ├── _readDict.AddOrUpdate per hashtag
│   ├── _postCount++
│   └── If _postCount >= _batchSize → TrySwapAndFlushAsync()
├── TrySwapAndFlushAsync()
│   ├── Non-blocking swap at BatchSize, hard block at MaxBatchSize
│   └── Fire background writer → KafkaHashtagPublisher.PublishAsync() → checkpoint
└── FlushRemainingAsync()  → shutdown: flush remaining _readDict
│
KafkaHashtagPublisher.cs
├── PublishAsync(dict, partitionId)
│   ├── For each (hashtag, count): SendWithRetryAsync()
│   │   └── EventHubProducerHandler.SendEventAsync (from Shared/Handlers/)
│   └── dict.Clear()
└── SendWithRetryAsync() → exponential backoff, 3 attempts
```

### Dependencies (NuGet)

- `Azure.Identity` — `DefaultAzureCredential`
- `Azure.Messaging.EventHubs` — core types
- `Azure.Messaging.EventHubs.Processor` — `EventProcessorClient`
- `Azure.Storage.Blobs` — checkpoint blob container
- `Microsoft.Azure.Cosmos` — Cosmos DB SDK v3 (transitive via `Shared.csproj`)
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
  "CosmosDb": {
    "Endpoint": "https://<YOUR_COSMOS>.documents.azure.com:443/",
    "DatabaseName": "HashtagServiceDb",
    "PostsContainerName": "Posts"
  },
  "Service": {
    "BatchSize": 100,
    "MaxBatchSize": 500
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
                                    ┌─────────────────┐
                                    │   Cosmos DB      │
                                    │   Posts container│
                                    └────────▲────────┘
                                             │ point read (1 RU)
  ┌──────────────────────────────────────────┼──────────────┐
  │                  HashTagCounter Instance  │              │
  │                                          │              │
  │  ┌──────────────────────┐     ┌──────────┼───────────┐  │
  │  │   EventProcessor     │     │   PartitionConsumer  │  │
  │  │   (posts-topic)      │────►│   (per partition)    │  │
  │  │   PostNotification   │     │                      │  │
  │  └──────────────────────┘     │  PostDocumentHandler │  │
  │                               │  fetch post by ID    │  │
  │                               │  ExtractHashtags()   │  │
  │                               │  ┌────────────────┐  │  │
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
  │                               │   KafkaHashtagPublisher  │
  │                               │   → EventHubProducerHandler
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
  - _publisher   : KafkaHashtagPublisher
  - _postHandler : PostDocumentHandler  — fetches posts from Cosmos DB

Methods:
  + ProcessEventAsync(Guid postId) : Task
      1. PostDocumentHandler.GetAsync(postId) → fetch PostDocument from Cosmos (1 RU)
      2. If not found → log warning, still increment _postCount, return
      3. Extract hashtags from postDoc.Text
      4. For each hashtag: _readDict.AddOrUpdate(tag, 1, (k,v) => v+1)
      5. _postCount++
      6. If _postCount >= _batchSize → TrySwapAndFlush()

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
Delegates to the shared `EventHubProducerHandler` (from `Shared/Handlers/`) for serialization + send.
Adds retry with exponential backoff (3 attempts) on top.

```
Field:
  - _producerHandler : EventHubProducerHandler  (wraps EventHubProducerClient)

Method: PublishAsync(dict, partitionId) : Task
───────────────────────────────────────────────
For each (hashtag, count) in dict:
  1. Build HashtagCount { Hashtag = hashtag, Count = count, Timestamp = now }
  2. SendWithRetryAsync → _producerHandler.SendEventAsync(payload, partitionKey=hashtag)
  3. Retry up to 3× with exponential backoff on transient errors
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
  │  receives PostNotification from partition
  │  fetches PostDocument from Cosmos DB (point read, 1 RU)
  │  extracts hashtags from postDoc.Text
  │  updates _readDict
  │  every BatchSize posts → TrySwapAndFlush()
  │    ├── writer free?  → swap + hand off, keep reading
  │    ├── writer busy?  → skip swap, keep reading (dict grows past BatchSize)
  │    └── hit MaxBatch? → hard block until writer finishes (safety valve)
  │
  └──► Thread 2 — Writer Thread (background, 1 per partition)
         │
         │  iterates _writeDict
         │  publishes {hashtag, count} via EventHubProducerHandler
         │  clears dict
         │  checkpoints
         │  releases semaphore
```

- **Reader never blocks on Kafka publish** under normal load — swap is non-blocking.
- **Reader blocks on Cosmos read** per event (~1ms per point read) — acceptable at this scale.
- **Writer is fire-and-forget** from reader's perspective, guarded by semaphore.
- If writer is slow, reader keeps accumulating past `BatchSize` up to `MaxBatchSize`.
- Hard block only at `MaxBatchSize` — safety valve to bound memory.
- On shutdown: reader finishes current post, triggers final swap+flush, waits for writer.

---

### Sequence Diagram — Steady State

```
Reader Thread              PartitionConsumer           Cosmos DB             Writer Thread          hashtags-topic
─────────────              ─────────────────           ─────────             ─────────────          ──────────────
  │ PostNotification             │                        │                      │                      │
  │──────────────────────────────►                        │                      │                      │
  │                    GetAsync(postId)───────────────────►│                      │                      │
  │                              │◄──── PostDocument ─────│                      │                      │
  │                    extract hashtags                    │                      │                      │
  │                    update readDict                     │                      │                      │
  │                    postCount++                         │                      │                      │
  │                              │                         │                      │                      │
  │  ... repeat 99 more times ...│                         │                      │                      │
  │                              │                         │                      │                      │
  │                    postCount == 100                    │                      │                      │
  │                    TrySwapAndFlush()                   │                      │                      │
  │                    _writerBusy.Wait(0) → true          │                      │                      │
  │                    swap(readDict, writeDict)           │                      │                      │
  │                    fire writer ────────────────────────┼──────────────────────►                      │
  │                              │                         │       for each (tag, cnt):                  │
  │  continue reading            │                         │       SendEventAsync(HashtagCount)          │
  │──────────────────────────────►                         │              │                              │
  │                    GetAsync(postId)───────────────────►│              │──── SendAsync ───────────────►│
  │                    update readDict                     │              │◄──────── ACK ───────────────│
  │                    postCount++                         │       dict.Clear()                          │
  │                              │                         │       checkpoint                            │
  │  ... writer finishes ...     │                         │       release semaphore                     │
```

---

## File / Class Layout

```
HashtagExtractor/
├── Program.cs                  — entry point, wiring (CosmosClient + EventProcessorClient)
├── PartitionConsumer.cs        — per-partition: Cosmos fetch + extract + count + swap logic
├── KafkaHashtagPublisher.cs    — publishes {hashtag, count} via shared EventHubProducerHandler
└── appsettings.json

Shared/Models/
├── PostNotification.cs         — INPUT: lightweight event from posts-topic ({PostId})
├── PostDocument.cs             — Cosmos DB document fetched by PartitionConsumer
├── HashtagCount.cs             — OUTPUT: aggregated count to hashtags-topic
├── Post.cs                     — domain model (used by PostCreator)
└── ...

Shared/Handlers/
├── PostDocumentHandler.cs      — REUSED: Cosmos CRUD for Posts container
├── EventHubProducerHandler.cs  — REUSED: generic Event Hub send wrapper
└── ...
```

---

## Shared Models & Handlers You Use

```csharp
// Shared/Models/PostNotification.cs — INPUT (from posts-topic)
public class PostNotification
{
    public Guid PostId { get; set; }
}

// Shared/Models/PostDocument.cs — FETCHED from Cosmos DB
public class PostDocument
{
    public string Id { get; set; }          // post GUID as string
    public string UserId { get; set; }
    public string Url { get; set; }
    public string Text { get; set; }        // contains #hashtags
    public DateTimeOffset CreatedAt { get; set; }
    // ... soft-delete fields
}

// Shared/Models/HashtagCount.cs — OUTPUT (to hashtags-topic)
public class HashtagCount
{
    public string Hashtag { get; set; } = string.Empty;   // lowercase, e.g. "dotnet"
    public long Count { get; set; }                        // aggregated count for this batch
    public DateTimeOffset Timestamp { get; set; }          // when this batch was produced
}
```

### Shared Handlers Reused

| Handler | From | Used For |
|---|---|---|
| `PostDocumentHandler` | `Shared/Handlers/` | Cosmos point read to fetch post by ID (1 RU) |
| `EventHubProducerHandler` | `Shared/Handlers/` | Serialization + send to `hashtags-topic` (wrapped by `KafkaHashtagPublisher` with retry) |

> **Partition key**: The `Hashtag` string is used as the Event Hubs partition key, ensuring all counts for the same hashtag arrive at the same downstream partition in `hashtags-topic`. This allows HashTagPersister to aggregate per-hashtag without cross-partition coordination.

---

## Error Handling

| Scenario | Handling |
|---|---|
| Deserialization failure | Log, skip notification, return |
| Post not found in Cosmos DB | Log warning, skip, still increment `postCount` |
| Cosmos transient error | Cosmos SDK retries internally; unrecoverable errors logged, skip post |
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
| Post not found in Cosmos (deleted or not yet replicated) | Log, skip, still increment `postCount` |
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
4. **Serialization** — `System.Text.Json` only (Event Hub payloads); Cosmos SDK handles its own serialization with camelCase.
5. **Error handling** — Log to `Console.Error`, never crash the processor loop.
6. **Checkpointing** — Checkpoint after each batch swap (~100 posts), not per event.
7. **Naming** — The agent is called HashTagCounter, the folder is HashtagExtractor. Use the agent name in conversation, the folder name in code.
8. **Testing** — If asked to add tests, create an `HashtagExtractor.Tests/` xUnit project.
9. **Cosmos DB — read only** — This service reads from the Posts container (point reads) but does **not write** to Cosmos.
10. **Back-pressure** — Non-blocking swap at `BatchSize`; hard block only at `MaxBatchSize`.
11. **Reuse shared handlers** — Use `PostDocumentHandler` and `EventHubProducerHandler` from `Shared/Handlers/`. Do not duplicate SDK plumbing.

---

## Upstream / Downstream

| Direction | Service | Topic / Store | Payload |
|---|---|---|---|
| ← reads from | PostCreator | `posts-topic` | `PostNotification` JSON (`{PostId}`) |
| ← reads from | Cosmos DB | `HashtagServiceDb` / `Posts` | `PostDocument` (point read by ID) |
| → writes to | HashTagPersister | `hashtags-topic` | `HashtagCount` JSON (`{Hashtag, Count, Timestamp}`) |
