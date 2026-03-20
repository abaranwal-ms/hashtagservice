# HashTagCounter — Requirements

> **Service**: HashTagCounter (project folder HashtagExtractor/)
> **Date**: 2026-03-21
> **Status**: Draft

---

## 1. Purpose

HashTagCounter is an Event Hubs (Kafka-protocol) consumer that reads posts,
extracts hashtags, **counts** their frequencies in memory, and periodically
publishes aggregated `{hashtag, count}` messages to the `hashtags-topic`
Event Hub (partitioned by hashtag).

---

## 2. Functional Requirements

### 2.1 Input — Kafka Consumer

| Aspect | Detail |
|---|---|
| Source topic | `posts-topic` (Azure Event Hubs, Kafka surface) |
| Partitions | **3** — topic is partitioned by `PostId` |
| Consumer group | `` |
| Threads | **3 reader threads**, one per partition |
| Partition affinity | Each thread is assigned a **fixed partition**. On crash/restart the same thread picks up the **same partition** via checkpointing. |
| Checkpoint store | Azure Blob Storage (one checkpoint per partition) |
| Payload | `Post` JSON (`Id`, `Url`, `Text`, `CreatedAt`) |

### 2.2 Hashtag Extraction

- For every consumed `Post`, extract all words beginning with `#`.
- Strip the `#` prefix, lowercase, deduplicate per post.
- The resulting list is the set of hashtags for that post.

### 2.3 In-Memory Counting (Double-Buffer Dictionary)

Each reader thread maintains **two** `ConcurrentDictionary<string, long>` instances:

| Name | Purpose |
|---|---|
| **Read dictionary** | Actively populated by the reader thread (extraction results go here). |
| **Write dictionary** | Handed off to a background writer thread that merges it into Cosmos DB. |

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

### 2.4 Output — Kafka Producer (`hashtags-topic`)

| Aspect | Detail |
|---|---|
| Target topic | `hashtags-topic` (Azure Event Hubs, Kafka surface) |
| Partitions | **3** — partitioned by **hashtag** (not PostId) |
| Message payload | `HashtagCount` JSON: `{ "Hashtag": "<tag>", "Count": <long>, "Timestamp": "<ISO8601>" }` |
| Partition key | The hashtag string (lowercase). Ensures all counts for the same hashtag land on the same partition. |
| Publish trigger | On dictionary swap — the writer thread publishes **one message per hashtag** in the write dictionary. |
| Concurrency | Each writer thread publishes its own dictionary slice; multiple writer threads may run concurrently for different source partitions. |

### 2.5 Downstream: HashTagPersister

> The downstream `HashTagPersister` service consumes from `hashtags-topic`
> and is responsible for merging counts into Cosmos DB. This service does
> **not** interact with Cosmos DB at all — it only reads from Kafka and
> writes to Kafka.

---

## 3. Non-Functional Requirements

| Category | Requirement |
|---|---|
| **Crash recovery** | On restart, each thread resumes from its last Blob checkpoint — same partition, no data loss. |
| **Consistency** | Checkpoint is updated **after** the 100-post batch is successfully accumulated (not after each message). |
| **Back-pressure** | If the writer is still busy at `BatchSize`, the reader **keeps reading** (non-blocking). Hard block only at `MaxBatchSize` (default 500) as a safety valve to bound memory. |
| **Thread safety** | The read dictionary is only written by one reader thread. The write dictionary is only read by one writer thread after swap. No cross-thread dictionary sharing without swap handoff. |
| **At-least-once delivery** | Kafka publishing is at-least-once. Downstream consumer (HashTagPersister) must handle duplicate `{hashtag, count}` messages idempotently. |
| **Graceful shutdown** | `Ctrl+C` / `SIGTERM` → finish current post, flush partial dictionary to DB, checkpoint, then exit. |
| **Configuration** | All tunables (batch size, thread count, connection strings) via `appsettings.json`. |

---

## 4. Open Questions / Decisions Needed

| # | Question | Resolution |
|---|---|---|
| Q1 | Should we move to a **multi-instance deployment** (1 process per partition) instead of 3 threads in 1 process? | **✅ Decided: Option B** — 1 thread, 1 process × N instances. Code also works as single process with multiple partitions for local dev. |
| Q2 | Output target? | **✅ Decided: Kafka** — publish `{hashtag, count}` to `hashtags-topic` (partitioned by hashtag). Cosmos DB merge is the responsibility of downstream HashTagPersister. |
| Q3 | What happens if a post contains 0 hashtags? | Skip, no count entry, still counts toward the batch threshold. |
| Q4 | Should the 100-post batch be configurable? | **✅ Yes** — `Service:BatchSize` (default 100) and `Service:MaxBatchSize` (default 500, safety cap). |
| Q5 | Should `SwapAndFlush` block or be non-blocking when writer is busy? | **✅ Decided: Non-blocking** — reader keeps accumulating past `BatchSize`; hard block only at `MaxBatchSize`. Avoids stalling `EventProcessorClient` and risking partition reassignment. |
