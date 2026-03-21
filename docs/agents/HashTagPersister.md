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
3. **Merges** the count into the existing `HashtagDocument` in Cosmos DB via a read-modify-write pattern (fetch existing → increment `TotalPostCount` → upsert).
4. **Checkpoints** in Azure Blob Storage every N events per partition (`CheckpointInterval`, default 10).

---

## Current State — ✅ IMPLEMENTED

The project is fully implemented:
- `Program.cs` — top-level entry point with `EventProcessorClient`, Cosmos read-modify-write, and batched checkpointing.
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

### Concurrency Model

- `EventProcessorClient` manages partition assignment and rebalancing automatically.
- No explicit threading — the processor dispatches events sequentially per partition.
- Shared `CosmosClient` (thread-safe) and shared `HashtagDocumentHandler`.
- Per-partition checkpoint counters tracked in `ConcurrentDictionary<string, int>`.

### Checkpoint Strategy

| Event | Checkpoint? |
|---|---|
| Each individual event | **No** |
| Every `CheckpointInterval` events per partition (default 10) | **Yes** |
| On graceful shutdown | Processor stops; pending events not checkpointed beyond last interval |

On crash, up to `CheckpointInterval - 1` events may be re-processed. This is safe because the merge is additive — re-adding the same counts is acceptable at the POC level.

> ⚠ **At-least-once delivery**: If the service crashes after upserting to Cosmos but before checkpointing, the same `HashtagCount` will be replayed on restart, double-counting. For production, add idempotency (e.g. batch sequence tracking). Acceptable for POC.

### Key Code Paths

```
Program.cs
├── Config load (appsettings.json)
├── CancellationTokenSource + Console.CancelKeyPress
├── CosmosClient (shared, thread-safe) via DefaultAzureCredential
│   └── GetContainer(databaseName, hashtagsContainerName)
│   └── HashtagDocumentHandler (shared)
├── BlobContainerClient → checkpoint storage
├── ConcurrentDictionary<string, int> checkpointCounters (per partition)
├── ConcurrentDictionary<string, long> processedCounters  (per partition, for logging)
├── EventProcessorClient → reads hashtags-topic
│   ├── ProcessEventAsync:
│   │   ├── Deserialize HashtagCount (System.Text.Json)
│   │   ├── Validate: skip null / empty hashtag
│   │   ├── HashtagDocumentHandler.GetAsync() → fetch or create new doc
│   │   ├── Increment doc.TotalPostCount += count
│   │   ├── HashtagDocumentHandler.UpdateAsync() → upsert to Cosmos
│   │   ├── Increment per-partition counter
│   │   └── If counter >= CheckpointInterval → UpdateCheckpointAsync, reset counter, log
│   └── ProcessErrorAsync → log to stderr
├── StartProcessingAsync → run until Ctrl+C
└── Graceful shutdown (StopProcessingAsync)
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
    "CheckpointInterval": 10
  }
}
```

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
├── Program.cs                  — entry point, all wiring + event processing logic
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
4. **No explicit threading** — `EventProcessorClient` handles partition dispatch. `CosmosClient` is thread-safe — share it.
5. **Serialization** — `System.Text.Json` for Event Hubs payloads; Cosmos SDK configured with camelCase serialization.
6. **Error handling** — Log to `Console.Error`, never crash the processor loop. Skip failed events.
7. **Checkpointing** — Checkpoint every `CheckpointInterval` events per partition (default 10), not per event.
8. **Naming** — Agent is called HashTagPersister, folder is `HashtagPersister/`.
9. **Testing** — If asked to add tests, create a `HashtagPersister.Tests/` xUnit project.
10. **Reuse shared handlers** — Use `HashtagDocumentHandler` from `Shared/Handlers/`. Do not duplicate SDK plumbing.

---

## Edge Cases

| Case | Behaviour |
|---|---|
| First time seeing a hashtag | `GetAsync` returns `null` → new `HashtagDocument` created with count |
| Duplicate event (at-least-once) | Count is added again — acceptable for POC, produces slightly inflated totals |
| Empty or null hashtag in message | Logged and skipped |
| Malformed JSON | Deserialization error logged, event skipped |
| Cosmos 429 (throttled) | Cosmos SDK retries internally; if still fails, `CosmosException` logged and event skipped |
| Crash after Cosmos write, before checkpoint | Events replayed on restart; counts double-counted (at-least-once) |

---

## Future Considerations

- **Idempotent delivery**: Track processed batch IDs or sequence numbers to prevent double-counting on replay.
- **TopPosts population**: Currently only `TotalPostCount` is incremented. `TopPosts` list is not populated by the persister — needs a separate enrichment path or changes to the `HashtagCount` payload.
- **Batch writes**: Accumulate multiple `HashtagCount` events for the same hashtag before writing to Cosmos to reduce RU consumption.
- **Transactional batch**: Use Cosmos transactional batch for multi-document writes if the schema evolves.
- **Retry policy**: Add explicit retry with exponential backoff for transient Cosmos errors instead of skipping.

---

## Upstream / Downstream

| Direction | Service | Topic / Store | Payload |
|---|---|---|---|
| ← reads from | HashTagCounter | `hashtags-topic` | `HashtagCount` JSON (`{Hashtag, Count, Timestamp}`) |
| → writes to | Cosmos DB | `HashtagServiceDb` / `Hashtags` | `HashtagDocument` (read-modify-write upsert) |
| ← read by | UserView | Cosmos DB | query layer on top |
