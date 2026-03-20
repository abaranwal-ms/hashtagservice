# HashTagCounter — Requirements

> **Service**: HashTagCounter (project folder HashtagExtractor/)
> **Date**: 2026-03-21
> **Status**: Draft

---

## 1. Purpose

HashTagCounter is an Event Hubs (Kafka-protocol) consumer that reads posts,
extracts hashtags, **counts** their frequencies, and periodically merges those
counts into Cosmos DB.

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
5. The writer thread merges the write dictionary into Cosmos DB, clears it, and releases the semaphore.
6. After the swap, the reader continues into the fresh (empty) read dictionary immediately.

### 2.4 Database Merge (Cosmos DB)

| Aspect | Detail |
|---|---|
| Database | Azure Cosmos DB (NoSQL / document) |
| Partition key | Hashtag string (the `#tag` itself) |
| Document shape | `{ "id": "<hashtag>", "hashtag": "<hashtag>", "count": <long>, "imageUrls": [...] }` |
| Merge logic | **Upsert**: if document exists → `count += localCount`; if not → insert with `count = localCount` and empty `imageUrls`. |
| `imageUrls` field | This service **never modifies** the `imageUrls` array. It must be preserved as-is during merge. |
| Concurrency | Each writer thread processes its own dictionary slice; multiple writer threads may run concurrently for different partitions. |

### 2.5 No Downstream Event Publishing

> **Change from current implementation**: The redesigned service does **not**
> publish `HashtagEvent` messages to `hashtags-topic`.  
> Counting and persisting happen inside this service. The `hashtags-topic`
> and downstream `HashTagPersister` are no longer needed for counts.

*(If event publishing is ever re-added it will be a separate concern.)*

---

## 3. Non-Functional Requirements

| Category | Requirement |
|---|---|
| **Crash recovery** | On restart, each thread resumes from its last Blob checkpoint — same partition, no data loss. |
| **Consistency** | Checkpoint is updated **after** the 100-post batch is successfully accumulated (not after each message). |
| **Back-pressure** | If the writer is still busy at `BatchSize`, the reader **keeps reading** (non-blocking). Hard block only at `MaxBatchSize` (default 500) as a safety valve to bound memory. |
| **Thread safety** | The read dictionary is only written by one reader thread. The write dictionary is only read by one writer thread after swap. No cross-thread dictionary sharing without swap handoff. |
| **Idempotency** | Cosmos DB upsert (read-then-write or patch) must handle concurrent writers for the same hashtag if multiple partitions encounter the same tag. |
| **Graceful shutdown** | `Ctrl+C` / `SIGTERM` → finish current post, flush partial dictionary to DB, checkpoint, then exit. |
| **Configuration** | All tunables (batch size, thread count, connection strings) via `appsettings.json`. |

---

## 4. Open Questions / Decisions Needed

| # | Question | Resolution |
|---|---|---|
| Q1 | Should we move to a **multi-instance deployment** (1 process per partition) instead of 3 threads in 1 process? | **✅ Decided: Option B** — 1 thread, 1 process × N instances. Code also works as single process with multiple partitions for local dev. |
| Q2 | Cosmos DB merge strategy? | **Option A**: Read doc → increment → replace (optimistic concurrency w/ ETag). Patch operations noted as future improvement. |
| Q3 | What happens if a post contains 0 hashtags? | Skip, no count entry, still counts toward the batch threshold. |
| Q4 | Should the 100-post batch be configurable? | **✅ Yes** — `Service:BatchSize` (default 100) and `Service:MaxBatchSize` (default 500, safety cap). |
| Q5 | Should `SwapAndFlush` block or be non-blocking when writer is busy? | **✅ Decided: Non-blocking** — reader keeps accumulating past `BatchSize`; hard block only at `MaxBatchSize`. Avoids stalling `EventProcessorClient` and risking partition reassignment. |
