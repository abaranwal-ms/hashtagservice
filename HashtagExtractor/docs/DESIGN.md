# HashTagCounter — Design Document

> **Service**: HashTagCounter (project folder `HashtagExtractor/`)
> **Date**: 2026-03-21
> **Status**: Draft — for review before implementation

---

## 1. Deployment Model Decision

### The Question

> *Should we redesign this to a multi-instance service, where each instance
> reads from one partition of Kafka?*

### Recommendation: **Yes — Option B (1 instance per partition)**

| Criteria | Option A: 3 threads / 1 process | Option B: 1 thread / 1 instance × N |
|---|---|---|
| Scaling | Fixed to 3; must redeploy to change | Scale out by adding instances |
| Failure blast radius | Process crash loses **all 3** partitions | Crash loses **1** partition only |
| Memory | 3 dict pairs share one heap | Each instance has own memory budget |
| Deployment | Single binary, simpler ops | Container Apps / K8s manages replicas |
| Partition affinity | Manual thread-to-partition mapping | Event Hubs consumer group handles it automatically |
| Complexity | Thread coordination, swap logic per-thread | Each instance is a simple single-threaded loop |
| Checkpoint | Must track per-thread | Natural: 1 checkpoint per instance |

**Verdict**: Option B is the cleaner design. Each instance is a simple loop:  
read → extract → count → swap → write → repeat.  
The Event Hubs consumer group protocol already guarantees that each partition
is assigned to exactly one consumer instance.  However, the code should still
work correctly if run as a single instance with 3 threads (Option A) for local
dev / testing.

### Design for both

We design the core logic as a **`PartitionConsumer` class** that is
single-threaded internally. Then:

- **Production**: deploy 3 instances, each picks up 1 partition via consumer
  group rebalancing.
- **Dev / local**: run 1 instance that internally spins up
  `EventProcessorClient` handling up to 3 partitions, each dispatched to its
  own `PartitionConsumer`.

---

## 2. High-Level Architecture

`
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
  │                               │   (DB merge)         │  │
  │                               └──────────┬───────────┘  │
  │                                          │              │
  └──────────────────────────────────────────┼──────────────┘
                                             │
                                    ┌────────▼────────┐
                                    │   Cosmos DB     │
                                    │  (hashtag docs) │
                                    └─────────────────┘
`

---

## 3. Component Design

### 3.1 `PartitionConsumer`

**Responsibility**: Processes events for exactly one partition.

`
Class: PartitionConsumer
─────────────────────────────────────
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
      // NON-BLOCKING swap — keeps reading if writer is still busy
      1. If _postCount >= _maxBatchSize:
           await _writerBusy.WaitAsync()       // hard block — safety valve
      2. Else if !_writerBusy.Wait(0):         // non-blocking try
           return                               // writer busy, keep reading
      3. Swap references: (_readDict, _writeDict) = (_writeDict, _readDict)
      4. _postCount = 0
      5. Hand _writeDict to WriterThread (fire-and-forget with semaphore release)

      Normal case (writer finishes fast):  swap at exactly BatchSize posts.
      Writer slightly slow:               reader keeps going, swaps at 101–499.
      Writer severely degraded:           hard block at MaxBatchSize (back-pressure).

  + FlushRemainingAsync() : Task
      // Called on shutdown — await _writerBusy, flush whatever is in _readDict
`

### 3.2 `WriterThread` (DB Merge Logic)

**Responsibility**: Takes a dictionary snapshot and merges into Cosmos DB.

`
Method: MergeToCosmosAsync(dict, partitionId) : Task
───────────────────────────────────────────────────
For each (hashtag, count) in dict:
  1. Try ReadItemAsync<HashtagDocument>(hashtag, partitionKey=hashtag)
  2. If found:
       doc.Count += count
       ReplaceItemAsync(doc, etag check)        // optimistic concurrency
  3. If not found:
       CreateItemAsync(new HashtagDocument {
           id = hashtag, hashtag = hashtag,
           count = count, imageUrls = []
       })
  4. On conflict (412 Precondition Failed) → retry from step 1
  5. Clear the dict entry after successful write

After all entries merged:
  dict.Clear()
  _writerBusy.Release()   // signal PartitionConsumer that write dict is free
`

### 3.3 `HashtagCount` (Kafka message payload)

`csharp
namespace HashtagService.Shared.Models;

public class HashtagCount
{
    public string Hashtag { get; set; } = string.Empty;   // lowercase, e.g. "dotnet"
    public long Count { get; set; }                        // aggregated count for this batch
    public DateTimeOffset Timestamp { get; set; }          // when this batch was produced
}
`

> **Partition key**: The `Hashtag` string is used as the Event Hubs partition
> key, ensuring all counts for the same hashtag arrive at the same downstream
> partition in `hashtags-topic`. This allows HashTagPersister to aggregate
> per-hashtag without cross-partition coordination.

### 3.4 Checkpoint Strategy

| Event | Checkpoint? |
|---|---|
| Each individual post read | **No** |
| After 100-post batch accumulated (swap triggered) | **Yes** — checkpoint the offset of the 100th post |
| On graceful shutdown | **Yes** — flush partial dict, then checkpoint |

This means on crash we may re-process up to 99 posts.  
That is safe because the merge is **additive-idempotent** only if we haven't
checkpointed yet — and we haven't, so the re-read + re-count is correct.

> ⚠ **Subtle point**: If the writer thread has already published to
> `hashtags-topic` but we crash before checkpointing, the same counts will
> be published again on restart. This is **at-least-once delivery** — the
> downstream HashTagPersister must handle duplicate `{hashtag, count}`
> messages idempotently (e.g., by tracking batch IDs or using idempotent
> upsert logic).

---

## 4. Threading Model (per instance)

`
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
`

- **Reader never blocks on Kafka publish** under normal load — swap is non-blocking.
- **Writer is fire-and-forget** from reader's perspective, guarded by semaphore.
- If writer is slow, reader keeps accumulating past `BatchSize` up to `MaxBatchSize`.
- Hard block only at `MaxBatchSize` — safety valve to bound memory.
- On shutdown: reader finishes current post, triggers final swap+flush, waits for writer.

---

## 5. Config Shape (`appsettings.json`)

`json
{
  "EventHub": {
    "Namespace": "<NS>.servicebus.windows.net",
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
`

> `CosmosDb` section removed — this service no longer writes to Cosmos DB.
> `HashtagsTopic` re-added — this service publishes aggregated counts to it.

---

## 6. File / Class Layout

`
HashtagExtractor/
├── Program.cs                  — entry point, wiring
├── PartitionConsumer.cs        — per-partition read + count + swap logic
├── KafkaHashtagPublisher.cs    — publishes {hashtag, count} to hashtags-topic
├── appsettings.json
└── docs/
    ├── REQUIREMENTS.md
    └── DESIGN.md               ← this file

Shared/Models/
├── Post.cs                     — input model (unchanged)
└── HashtagCount.cs             — output model (new: {Hashtag, Count, Timestamp})
`

---

## 7. Sequence Diagram — Steady State

`
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
  │                              │              create EventDataBatch           │
  │  continue reading            │              for each (tag, cnt):            │
  │──────────────────────────────►              add {tag, cnt, ts}              │
  │                    update readDict              │                      │
  │                    postCount++                  │──── SendBatchAsync ───►│
  │                              │                  │◄──────── ACK ─────────│
  │  ... writer still busy ...   │              dict.Clear()                   │
  │                    postCount == 200             release semaphore            │
  │                    TrySwapAndFlush()                    │                      │
  │                    _writerBusy.Wait(0) → false          │                      │
  │                    skip swap, keep reading              │                      │
  │──────────────────────────────►                          │                      │
  │                    update readDict                      │                      │
  │                              │                          │                      │
  │  ... next post ...           │                          │                      │
  │                    postCount == 201                      │                      │
  │                    TrySwapAndFlush()                    │                      │
  │                    _writerBusy.Wait(0) → true           │                      │
  │                    swap + fire writer  ─────────────────►                      │
  │                              │                          │                      │
`

---

## 8. Error Handling

| Scenario | Handling |
|---|---|
| Deserialization failure | Log, skip post, still increment postCount |
| Kafka publish transient error | Retry with exponential backoff (3 attempts) |
| Kafka publish timeout | Retry; if exhausted, log and release semaphore (batch lost, will re-accumulate from checkpoint) |
| Event Hub disconnect (consumer) | `EventProcessorClient` reconnects automatically |
| Event Hub disconnect (producer) | `EventHubProducerClient` reconnects automatically |
| Writer thread exception | Log, release semaphore, reader will retry on next swap |

---

## 9. Future Considerations

- **Idempotent delivery**: Add a batch ID or sequence number to each
  `HashtagCount` message so the downstream HashTagPersister can deduplicate
  on crash/replay.
- **Windowed counting**: Aggregate by time window (e.g., 1-minute tumbling)
  for trending analytics before publishing.
- **Batch compression**: Enable Event Hubs compression for the producer to
  reduce network overhead on large batches.
- **Schema evolution**: If `HashtagCount` gains new fields, use a schema
  registry or versioned JSON to avoid breaking downstream consumers.

---

## 10. Next Steps

1. ✅ Review and finalize this design.
2. Create `HashtagCount` model in `Shared/Models/`.
3. Implement `KafkaHashtagPublisher`.
4. Implement `PartitionConsumer`.
5. Rewire `Program.cs`.
6. Update `appsettings.json` and `.csproj` (no Cosmos SDK needed).
7. Test locally with 1 instance / 3 partitions.
