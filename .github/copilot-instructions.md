# HashtagService — System-Wide Copilot Instructions

> These instructions are **automatically loaded** for every Copilot interaction in this repo.
> For service-specific deep dives, invoke one of the four agent prompts below.

---

## Quick-Reference: Agent Prompts

### Service Agents

| Agent | Invoke with | Scope |
|---|---|---|
| **PostCreator** | `#PostCreator` | PostGenerator project — fake social-post producer |
| **HashTagCounter** | `#HashTagCounter` | HashtagExtractor project — extracts & counts hashtags |
| **HashTagPersister** | `#HashTagPersister` | HashtagPersister project — writes hashtag data to Cosmos DB |
| **UserView** | `#UserView` | UserView project (NEW) — query API for trending hashtags |

### Infrastructure Agents

| Agent | Invoke with | Scope |
|---|---|---|
| **Kafka** | `#Kafka` | Event Hubs namespace, topics, partitions, consumer groups, Blob checkpoints |
| **Database** | `#Database` | Cosmos DB account, containers, partition keys, indexing, queries, RBAC |

---

## Architecture Overview

```
                                   Azure Event Hubs (3 partitions each)
                                   ┌──────────────┐    ┌──────────────────┐
  PostCreator ─── N threads ──────►│  posts-topic  │───►│  HashTagCounter  │
  (PostGenerator/)                 └──────────────┘    │  (HashtagExtractor/)
                                                       └────────┬─────────┘
                                                                │
                                                       ┌────────▼─────────┐
                                                       │  hashtags-topic  │
                                                       └────────┬─────────┘
                                                                │
                                                       ┌────────▼─────────┐
                                                       │ HashTagPersister │──► Azure Cosmos DB
                                                       │ (HashtagPersister/)   (document DB, partitioned)
                                                       └──────────────────┘
                                                                │
                                                       ┌────────▼─────────┐
                                                       │    UserView      │◄── reads from Cosmos DB
                                                       │   (UserView/)         (query / trending API)
                                                       └──────────────────┘
```

## Project ↔ Agent Name Mapping

| Folder / .csproj | Agent Name | Notes |
|---|---|---|
| `PostGenerator/` | **PostCreator** | Name in code is PostGenerator |
| `HashtagExtractor/` | **HashTagCounter** | Will be refactored to also count |
| `HashtagPersister/` | **HashTagPersister** | Stub — needs full implementation |
| `UserView/` | **UserView** | Not yet created |
| `Shared/` | (shared by all) | Models: `Post`, `HashtagEvent` |

## Azure Services

| Concept | Azure Service | Details |
|---|---|---|
| Message broker (2 topics) | **Azure Event Hubs** | `posts-topic`, `hashtags-topic` — 3 partitions each |
| Document database | **Azure Cosmos DB** | Partitioned container; stores hashtag documents |
| Checkpoint store | **Azure Blob Storage** | One container per consumer service |
| Identity | **DefaultAzureCredential** | Works with `az login`, Managed Identity, etc. |

## Shared Models (Shared/)

```csharp
// Post.cs
public class Post
{
    public Guid Id { get; set; }
    public string Url { get; set; }       // CDN-style URL
    public string Text { get; set; }       // ~10 words, some #tagged
    public DateTimeOffset CreatedAt { get; set; }
}

// HashtagEvent.cs
public class HashtagEvent
{
    public Guid PostId { get; set; }
    public List<string> Hashtags { get; set; }
    public DateTimeOffset ExtractedAt { get; set; }
}
```

## Tech Stack

- **.NET 8** (net8.0), C#, top-level `Program.cs` (no Startup class)
- **Azure.Messaging.EventHubs** — producer + `EventProcessorClient` consumer
- **Microsoft.Azure.Cosmos** — Cosmos DB SDK v3
- **Azure.Identity** — `DefaultAzureCredential`
- **Microsoft.Extensions.Configuration.Json** — `appsettings.json`-based config
- Concurrency via `Task.Run` threads + `SemaphoreSlim`

## Conventions

- Config is loaded from `appsettings.json` via `ConfigurationBuilder`.
- Each service uses `Console.CancelKeyPress` + `CancellationTokenSource` for graceful shutdown.
- Thread count, batch sizes, intervals are configurable.
- All projects reference `Shared/Shared.csproj` for models.
- Namespace: `HashtagService.Shared.Models` for shared types.
