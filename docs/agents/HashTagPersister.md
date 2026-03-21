# HashTagPersister Agent

You are **HashTagPersister**, the agent responsible for the **HashtagPersister** project.

---

## Your Identity

| Field | Value |
|---|---|
| Agent name | HashTagPersister |
| Project folder | `HashtagPersister/` |
| Entry point | `HashtagPersister/Program.cs` |
| Config file | `HashtagPersister/appsettings.json` |
| csproj | `HashtagPersister/HashtagPersister.csproj` |

---

## What This Service Does

HashTagPersister is an **Event Hubs consumer + Cosmos DB writer** that:

1. **Reads** serialized `HashtagCount` JSON messages from the `hashtags-topic` Event Hub using `EventProcessorClient`.
2. **Deserializes** each event to get a single hashtag name and its aggregated count.
3. **Accumulates** counts in a per-partition **double-buffer dictionary** — the reader never blocks on Cosmos writes.
4. At `BatchSize` boundaries the buffers **swap**: the reader continues into an empty dictionary while a background writer **flushes** the full dictionary to Cosmos DB via parallel read-modify-write upserts.
5. **Checkpoints** in Azure Blob Storage **after** each flush completes (not at swap time), so only persisted data is checkpointed.

---

## Current State — ✅ IMPLEMENTED

The project is fully implemented:
- `Program.cs` — top-level entry point with `EventProcessorClient`, deserialization, and per-partition `PartitionBuffer` wiring.
- `PartitionBuffer.cs` — double-buffer dictionary class: accumulate → swap → background flush → checkpoint.
- `appsettings.json` — fully populated with live Event Hub, Blob, and Cosmos DB endpoints.
- `.csproj` — references `Shared.csproj` + all required NuGet packages.

---

## Current Implementation — Detailed

### Input Payload

Each message on `hashtags-topic` is a `HashtagCount` produced by HashTagCounter:
- **Hashtag** — lowercase hashtag string (e.g. `"dotnet"`)
- **Count** — aggregated occurrences from one batch swap
- **Timestamp** — when the batch was produced

### Cosmos DB Write Pattern

**Read-modify-write** (not blind upsert):
1. `HashtagDocumentHandler.GetAsync(hashtag)` — point read (1 RU). Returns `null` if new.
2. If `null`, create a new `HashtagDocument { Hashtag = hashtag }`.
3. Increment `doc.TotalPostCount += hashtagCount.Count`.
4. `HashtagDocumentHandler.UpdateAsync(doc)` — upsert back to Cosmos.

This preserves existing `TopPosts` and soft-delete state on the document.

### Concurrency Model — Double-Buffer

Each partition gets its own `PartitionBuffer` instance (created lazily via `ConcurrentDictionary.GetOrAdd()`). The buffer maintains **two** `ConcurrentDictionary<string, long>` instances and a `SemaphoreSlim(1,1)` to coordinate the reader and writer.

| Name | Purpose |
|---|---|
| **Read dictionary** | Actively populated by the reader (event handler adds/increments hashtag counts here) |
| **Write dictionary** | Handed off to a background `Task.Run` that flushes to Cosmos DB in parallel |

- `EventProcessorClient` manages partition assignment and rebalancing automatically.
- The processor dispatches events sequentially per partition — so the read dictionary has no concurrent access.
- One `PartitionBuffer` per partition. One background writer thread per partition (guarded by semaphore).
- Shared `CosmosClient` (thread-safe) and shared `HashtagDocumentHandler`.
- Cosmos writes within a flush run with `Parallel.ForEachAsync` capped at `MaxFlushConcurrency` (default 5).

### Double-Buffer Protocol

1. Reader accumulates hashtag counts in the *read dictionary* (instant in-memory update).
2. After `BatchSize` events (default 50), the reader **attempts** a swap:
   - **Non-blocking try**: `_writerBusy.Wait(0)` — check if the writer thread is free.
   - **Writer free** → swap read ↔ write references, fire background writer, reset event count.
   - **Writer busy** → **skip the swap, keep reading**. The read dictionary grows past `BatchSize`.
3. On every subsequent event (while `_eventCount >= BatchSize`), try the swap again.
4. **Safety cap** (`MaxBatchSize`, default 250): if the read dictionary reaches this size and the writer is *still* busy, the reader **hard-blocks** (`await _writerBusy.WaitAsync()`) until the writer finishes. This bounds memory.
5. The writer thread flushes each `{hashtag, count}` entry via parallel Cosmos read-modify-write, checkpoints, clears the dictionary, and releases the semaphore.
6. After the swap, the reader continues into the fresh (empty) read dictionary immediately.

**Swap behaviour summary:**
- Normal (writer finishes fast): swap at exactly `BatchSize` events.
- Writer slightly slow: reader keeps going, swaps at 51–249 events.
- Writer severely degraded: hard block at `MaxBatchSize` (back-pressure).

```
Reader Thread              PartitionBuffer              Writer Thread          Cosmos DB
─────────────              ───────────────              ─────────────          ─────────
  │ HashtagCount                │                           │                     │
  │─────────────────────────────►                           │                     │
  │                   readDict[hashtag] += count            │                     │
  │                   eventCount++                          │                     │
  │                              │                          │                     │
  │  ... repeat BatchSize-1x ... │                          │                     │
  │                              │                          │                     │
  │                   eventCount == BatchSize                │                     │
  │                   _writerBusy.Wait(0) → true            │                     │
  │                   swap(readDict, writeDict)             │                     │
  │                   fire writer ──────────────────────────►│                     │
  │                              │                          │  Parallel.ForEach:  │
  │  continue reading            │                          │  GetAsync(tag) ────►│
  │─────────────────────────────►│                          │  ◄── doc ──────────│
  │                   readDict[hashtag] += count            │  doc.Count += n     │
  │                   eventCount++                          │  UpdateAsync(doc) ──►│
  │                              │                          │  ◄── ACK ─────────│
  │                              │                          │  checkpoint         │
  │                              │                          │  dict.Clear()       │
  │  ... writer finishes ...     │                          │  release semaphore  │
```

### Checkpoint Strategy

| Event | Checkpoint? |
|---|---|
| Each individual event | **No** |
| After buffer flush completes (every ~`BatchSize` events) | **Yes** — checkpoint the offset of the last event before the swap |
| On graceful shutdown | **Yes** — `FlushRemainingAsync` waits for in-progress writer, flushes read dict, then service exits |

Checkpointing happens **after** the Cosmos writes complete (inside the background writer), not at swap time. This ensures we never checkpoint data that hasn't been persisted.

On crash, up to `BatchSize - 1` events may be re-processed. This is safe because the merge is additive — re-adding the same counts is acceptable at the POC level.

> ⚠ **At-least-once delivery**: If the writer has flushed to Cosmos but crashes before calling `UpdateCheckpointAsync`, the same counts will be replayed on restart, double-counting. For production, add idempotency (e.g. batch sequence tracking). Acceptable for POC.

### Threading Model (per partition)

```
Thread 1 — Reader Thread (ProcessEventAsync, sequential per partition)
  │
  │  deserializes HashtagCount
  │  updates _readDict[hashtag] += count
  │  every BatchSize events → TrySwapAndFlushAsync()
  │    ├── writer free?  → swap + hand off, keep reading
  │    ├── writer busy?  → skip swap, keep reading (dict grows past BatchSize)
  │    └── hit MaxBatch? → hard block until writer finishes (safety valve)
  │
  └──► Thread 2 — Writer Thread (background, 1 per partition)
         │
         │  Parallel.ForEachAsync over writeDict (MaxFlushConcurrency)
         │    GetAsync(hashtag) → doc.TotalPostCount += count → UpdateAsync(doc)
         │  checkpoint (UpdateCheckpointAsync)
         │  dict.Clear()
         │  release semaphore
```

### Key Code Paths

```
Program.cs
├── Config load (appsettings.json)
├── CancellationTokenSource + Console.CancelKeyPress
├── CosmosClient (shared, thread-safe) via DefaultAzureCredential
│   └── GetContainer(databaseName, hashtagsContainerName)
│   └── HashtagDocumentHandler (shared)
├── BlobContainerClient → checkpoint storage
├── ConcurrentDictionary<string, PartitionBuffer> (lazily created per partition)
├── EventProcessorClient → reads hashtags-topic
│   ├── ProcessEventAsync:
│   │   ├── Deserialize HashtagCount (System.Text.Json)
│   │   ├── Validate: skip null / empty hashtag
│   │   └── PartitionBuffer.AccumulateAsync(hashtag, count, checkpointFunc)
│   └── ProcessErrorAsync → log to stderr
├── StartProcessingAsync → run until Ctrl+C
└── Graceful shutdown:
    ├── StopProcessingAsync()
    └── For each partition: PartitionBuffer.FlushRemainingAsync()

PartitionBuffer.cs (per partition)
├── AccumulateAsync(hashtag, count, checkpointFunc, ct)
│   ├── _readDict.AddOrUpdate(hashtag, count, += count)
│   ├── _eventCount++
│   └── If _eventCount >= _batchSize → TrySwapAndFlushAsync()
├── TrySwapAndFlushAsync(checkpointFunc, ct)
│   ├── >= MaxBatchSize?  → await _writerBusy.WaitAsync()  (hard block)
│   ├── else !Wait(0)?    → return  (writer busy, keep reading)
│   ├── Swap (_readDict, _writeDict)
│   ├── _eventCount = 0
│   └── Task.Run background writer:
│       ├── FlushDictAsync(_writeDict) → Parallel.ForEachAsync
│       │   └── GetAsync → doc.TotalPostCount += count → UpdateAsync
│       ├── checkpointFunc(ct)
│       ├── _writeDict.Clear()
│       └── _writerBusy.Release()
└── FlushRemainingAsync(ct)
    ├── await _writerBusy.WaitAsync()  (wait for in-progress writer)
    ├── FlushDictAsync(_readDict)
    ├── _readDict.Clear()
    └── _writerBusy.Release()
```

### Error Handling

| Scenario | Handling |
|---|---|
| JSON deserialization failure | Log to `Console.Error`, skip event, return |
| Null or empty hashtag | Log to `Console.Error`, skip event, return |
| Cosmos transient error (`CosmosException`) | Log status code + message to `Console.Error`, skip event (no retry) |
| Unexpected exception | Log to `Console.Error`, skip event |
| `OperationCanceledException` | Silently return (shutdown in progress) |
| Event Hub partition error | Logged via `ProcessErrorAsync` to `Console.Error` |

### Config Shape (`appsettings.json`)

```json
{
  "EventHub": {
    "Namespace": "<YOUR_NAMESPACE>.servicebus.windows.net",
    "HashtagsTopic": "hashtags-topic",
    "ConsumerGroup": "$Default"
  },
  "CheckpointStorage": {
    "BlobUri": "https://<STORAGE>.blob.core.windows.net/persister-checkpoints"
  },
  "CosmosDb": {
    "Endpoint": "https://<YOUR_COSMOS>.documents.azure.com:443/",
    "DatabaseName": "HashtagServiceDb",
    "HashtagsContainerName": "Hashtags"
  },
  "Service": {
    "BatchSize": 50,
    "MaxBatchSize": 250,
    "MaxFlushConcurrency": 5
  }
}
```

| Key | Default | Description |
|---|---|---|
| `BatchSize` | 50 | Events accumulated before attempting a buffer swap + flush |
| `MaxBatchSize` | 250 | Safety cap — hard-blocks reader if writer is still busy (bounds memory) |
| `MaxFlushConcurrency` | 5 | Max parallel Cosmos read-modify-write operations per flush |

---

## Shared Models You Consume

```csharp
// Shared/Models/HashtagCount.cs — INPUT (from hashtags-topic)
public class HashtagCount
{
    public string Hashtag { get; set; } = string.Empty;   // lowercase, e.g. "dotnet"
    public long Count { get; set; }                        // aggregated count for this batch
    public DateTimeOffset Timestamp { get; set; }          // when this batch was produced
}

// Shared/Models/HashtagDocument.cs — READ + WRITE (Cosmos DB, Hashtags container)
public class HashtagDocument
{
    public string Id => Hashtag;                           // id == hashtag (computed)
    public string Hashtag { get; set; }
    public List<HashtagPostRef> TopPosts { get; set; }
    public long TotalPostCount { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
```

### Shared Handlers Reused

| Handler | From | Used For |
|---|---|---|
| `HashtagDocumentHandler` | `Shared/Handlers/` | Cosmos point read (`GetAsync`) + upsert (`UpdateAsync`) for the Hashtags container |

---

## Dependencies (NuGet)

- `Azure.Identity` — `DefaultAzureCredential`
- `Azure.Messaging.EventHubs.Processor` — `EventProcessorClient`
- `Azure.Storage.Blobs` — checkpoint container
- `Microsoft.Azure.Cosmos` — Cosmos DB SDK v3
- `Microsoft.Extensions.Configuration.Json` — config

---

## File / Class Layout

```
HashtagPersister/
├── Program.cs                  — entry point, wiring (EventProcessorClient + PartitionBuffer)
├── PartitionBuffer.cs          — double-buffer: accumulate → swap → background flush → checkpoint
├── appsettings.json            — Event Hub, Blob, Cosmos DB, service config
└── HashtagPersister.csproj     — net8.0, references Shared.csproj

Shared/Models/
├── HashtagCount.cs             — INPUT: aggregated count from HashTagCounter
├── HashtagDocument.cs          — Cosmos DB document for Hashtags container
└── ...

Shared/Handlers/
├── HashtagDocumentHandler.cs   — REUSED: Cosmos CRUD for Hashtags container
└── ...
```

---

## Rules for This Agent

1. **Scope** — Only modify files inside `HashtagPersister/` and `Shared/` (if shared model changes are needed).
2. **Style** — Top-level `Program.cs`, no Startup class, `ConfigurationBuilder` pattern.
3. **Cosmos writes** — Use `UpsertItemAsync` (via `HashtagDocumentHandler.UpdateAsync`) for idempotency. Always supply the partition key.
4. **Threading** — One `PartitionBuffer` per partition with `SemaphoreSlim`-guarded background writer. Reader never blocks under normal load.
5. **Serialization** — `System.Text.Json` for Event Hubs payloads; Cosmos SDK configured with camelCase serialization.
6. **Error handling** — Log to `Console.Error`, never crash the processor loop. Skip failed events during flush.
7. **Checkpointing** — Checkpoint after each buffer flush completes (every ~`BatchSize` events), not per event. Checkpoint is called by the writer *after* Cosmos writes succeed.
8. **Back-pressure** — Non-blocking swap at `BatchSize`; hard block only at `MaxBatchSize`.
9. **Naming** — Agent is called HashTagPersister, folder is `HashtagPersister/`.
10. **Testing** — If asked to add tests, create a `HashtagPersister.Tests/` xUnit project.
11. **Reuse shared handlers** — Use `HashtagDocumentHandler` from `Shared/Handlers/`. Do not duplicate SDK plumbing.

---

## Edge Cases

| Case | Behaviour |
|---|---|
| First time seeing a hashtag | `GetAsync` returns `null` → new `HashtagDocument` created with count |
| Duplicate event (at-least-once) | Count is added again — acceptable for POC, produces slightly inflated totals |
| Empty or null hashtag in message | Logged and skipped |
| Malformed JSON | Deserialization error logged, event skipped |
| Cosmos 429 (throttled) | Cosmos SDK retries internally; if still fails, `CosmosException` logged, that hashtag skipped during flush |
| Crash after Cosmos write, before checkpoint | Events replayed on restart; counts double-counted (at-least-once) |
| Writer busy at `BatchSize` | Non-blocking skip — reader keeps accumulating in read dict |
| Writer busy at `MaxBatchSize` | Hard block until writer finishes (back-pressure safety valve to bound memory) |
| Graceful shutdown with buffered data | `FlushRemainingAsync` waits for in-progress writer, then flushes read dict |
| Single flush error for one hashtag | Only that hashtag's count is lost; other hashtags in the batch proceed |

---

## Future Considerations

- **Idempotent delivery**: Track processed batch IDs or sequence numbers to prevent double-counting on replay.
- **TopPosts population**: Currently only `TotalPostCount` is incremented. `TopPosts` list is not populated by the persister — needs a separate enrichment path or changes to the `HashtagCount` payload.
- **Transactional batch**: Use Cosmos transactional batch for multi-document writes if the schema evolves.
- **Retry policy**: Add explicit retry with exponential backoff for transient Cosmos errors instead of skipping during flush.
- **Flush timeout**: Add a time-based flush trigger (e.g., flush every 5 seconds even if `BatchSize` hasn't been reached) to bound latency during low-throughput periods.

---

## Upstream / Downstream

| Direction | Service | Topic / Store | Payload |
|---|---|---|---|
| ← reads from | HashTagCounter | `hashtags-topic` | `HashtagCount` JSON (`{Hashtag, Count, Timestamp}`) |
| → writes to | Cosmos DB | `HashtagServiceDb` / `Hashtags` | `HashtagDocument` (read-modify-write upsert) |
| ← read by | UserView | Cosmos DB | query layer on top |
