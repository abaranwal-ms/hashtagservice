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

### 3.3 `HashtagDocument` (Cosmos DB shape)

`csharp
public class HashtagDocument
{
    [JsonPropertyName("id")]
    public string Id { get; set; }            // the hashtag itself (lowercase)

    [JsonPropertyName("hashtag")]
    public string Hashtag { get; set; }        // same as id, human-readable

    [JsonPropertyName("count")]
    public long Count { get; set; }

    [JsonPropertyName("imageUrls")]
    public List<string> ImageUrls { get; set; } = new();  // NOT modified by this service
}
`

> **Partition key**: `/hashtag`

### 3.4 Checkpoint Strategy

| Event | Checkpoint? |
|---|---|
| Each individual post read | **No** |
| After 100-post batch accumulated (swap triggered) | **Yes** — checkpoint the offset of the 100th post |
| On graceful shutdown | **Yes** — flush partial dict, then checkpoint |

This means on crash we may re-process up to 99 posts.  
That is safe because the merge is **additive-idempotent** only if we haven't
checkpointed yet — and we haven't, so the re-read + re-count is correct.

> ⚠ **Subtle point**: If the writer thread has already merged to Cosmos but
> we crash before checkpointing, we will double-count up to 100 posts.
> Accepted trade-off for simplicity. Can be fixed later with a
> transactional outbox or by storing a `lastCheckpoint` in the Cosmos doc.

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
         │  upserts to Cosmos DB
         │  clears dict
         │  releases semaphore
`

- **Reader never blocks on Cosmos** under normal load — swap is non-blocking.
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
    "ConsumerGroup": ""
  },
  "CosmosDb": {
    "Endpoint": "https://<ACCOUNT>.documents.azure.com:443/",
    "DatabaseName": "hashtagdb",
    "ContainerName": "hashtags"
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

> `HashtagsTopic` is removed — this service no longer publishes events.

---

## 6. File / Class Layout

`
HashtagExtractor/
├── Program.cs                  — entry point, wiring
├── PartitionConsumer.cs        — per-partition read + count + swap logic
├── CosmosHashtagWriter.cs      — Cosmos DB merge logic
├── Models/
│   └── HashtagDocument.cs      — Cosmos document shape
├── appsettings.json
└── docs/
    ├── REQUIREMENTS.md
    └── DESIGN.md               ← this file
`

---

## 7. Sequence Diagram — Steady State

`
Reader Thread              PartitionConsumer           Writer Thread           Cosmos DB
─────────────              ─────────────────           ─────────────           ─────────
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
  │                              │                   for each (tag,cnt)           │
  │  continue reading            │                          │────── read doc ─────►│
  │──────────────────────────────►                          │◄──── doc / 404 ──────│
  │                    update readDict                      │────── upsert ───────►│
  │                    postCount++                          │◄──── 200 OK ─────────│
  │                              │                          │                      │
  │  ... writer still busy ...   │                          │   ... more tags ...   │
  │                    postCount == 200                     │                      │
  │                    TrySwapAndFlush()                    │                      │
  │                    _writerBusy.Wait(0) → false          │                      │
  │                    skip swap, keep reading              │                      │
  │──────────────────────────────►                          │                      │
  │                    update readDict                      │                      │
  │                              │                   release semaphore             │
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
| Cosmos DB transient error | Retry with exponential backoff (3 attempts) |
| Cosmos DB 412 (ETag conflict) | Re-read doc, re-increment, retry |
| Cosmos DB 429 (throttled) | Honor `RetryAfter` header |
| Event Hub disconnect | `EventProcessorClient` reconnects automatically |
| Writer thread exception | Log, release semaphore, reader will retry on next swap |

---

## 9. Future Considerations

- **Transactional outbox**: To eliminate the double-count window, write
  checkpoint + counts atomically (e.g., store checkpoint offset inside Cosmos).
- **Windowed counting**: Aggregate by time window (e.g., 1-minute tumbling)
  for trending analytics.
- **Patch operations**: Use Cosmos DB partial update (`PatchItemAsync`) for
  atomic `count += N` without read-modify-write.

---

## 10. Next Steps

1. ✅ Review and finalize this design.
2. Create `HashtagDocument` model in `HashtagExtractor/Models/`.
3. Implement `CosmosHashtagWriter`.
4. Implement `PartitionConsumer`.
5. Rewire `Program.cs`.
6. Update `appsettings.json` and `.csproj` (add Cosmos SDK).
7. Test locally with 1 instance / 3 partitions.
