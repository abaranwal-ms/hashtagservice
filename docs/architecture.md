# HashtagService — Architecture Diagram

## 1. Read Path

Below is the user-facing read (query) path, demonstrating how clients query trending hashtags and associated posts via the APIs.

```mermaid
flowchart LR
    subgraph UserViewService ["🌎 UserView Service (API)"]
        API_H["GET /api/hashtags/{tag}/posts"]
        API_T["GET /api/hashtags/trending"]
    end

    subgraph CosmosDB ["🗄️ Azure Cosmos DB"]
        direction TB
        HTAGS[("Hashtags container<br/>/hashtag")]
        POSTS[("Posts container<br/>/id")]
    end

    API_T -. "Query: SELECT * FROM c ORDER BY..." .-> HTAGS
    API_H -. "Point Read (Partition=tag)" .-> HTAGS
    API_H -. "Resolve post IDs" .-> POSTS

    classDef db fill:#d4edda,stroke:#198754,stroke-width:2px,color:#000
    classDef api fill:#e2d9f3,stroke:#6f42c1,stroke-width:2px,color:#000
    
    class HTAGS,POSTS db
    class API_H,API_T api
```

![Hashtag Posts Table UI](./images/HashTag-Posts.png)

<br/>

## 2. Write Path

Below is the event-driven ingestion path from generating raw posts to storing optimized search documents in the database.

```mermaid
flowchart TD
    subgraph PostGeneratorService ["📦 PostGenerator Service"]
        direction TB
        PC["PostCreator<br/>(N producer threads)"]
    end

    subgraph EventHubs ["🔄 Azure Event Hubs (Kafka)"]
        direction TB
        PTOPIC[("posts-topic<br/>3 Partitions")]
        HTOPIC[("hashtags-topic<br/>3 Partitions")]
    end

    subgraph HashtagExtractorService ["📦 HashtagExtractor Service"]
        direction TB
        HC["HashTagCounter<br/>(1 consumer / partition)"]
        BUF["In-memory buffer<br/>(swap every ~100 posts)"]
        HC -->|extract & aggregate| BUF
    end

    subgraph PostPersisterService ["📦 PostPersister Service"]
        direction TB
        PP["PostConsumer<br/>(reads raw posts)"]
    end

    subgraph HashtagPersisterService ["📦 HashtagPersister Service"]
        direction TB
        HP["HashTagPersister<br/>(reads trending tags)"]
    end

    subgraph CosmosDB ["🗄️ Azure Cosmos DB"]
        direction TB
        POSTS[("Posts container<br/>/id")]
        HTAGS[("Hashtags container<br/>/hashtag")]
    end

    %% ── Flows ──────────────────────────────────────────────

    PC -- "Post JSON<br/>partitionKey = postId" --> PTOPIC

    %% Dual Consumers reading from posts-topic
    PTOPIC -- "Consumer Group 1" --> HC
    PTOPIC -- "Consumer Group 2" --> PP

    %% Extractor aggregates and produces to hashtags-topic
    BUF -- "{hashtag, count, timestamp}<br/>partitionKey = hashtag<br/>(flush batch)" --> HTOPIC

    %% PostPersister inserts to Posts container
    PP -- "Upsert PostDocument" --> POSTS

    %% HashtagPersister consumes hashtags-topic and inserts to Hashtags container
    HTOPIC -- "Consumer Group 1" --> HP
    HP -- "UpsertItemAsync<br/>(additive merge)" --> HTAGS

    %% ── Styling ────────────────────────────────────────────

    classDef topic fill:#fff3cd,stroke:#ffc107,stroke-width:2px,color:#000
    classDef service fill:#d1ecf1,stroke:#0dcaf0,stroke-width:2px,color:#000
    classDef db fill:#d4edda,stroke:#198754,stroke-width:2px,color:#000

    class PTOPIC,HTOPIC topic
    class PC,HC,BUF,PP,HP service
    class POSTS,HTAGS db
```

## Data Flow Summary

| Stage | Component | Reads From | Writes To | Partition Key | Notes |
|---|---|---|---|---|---|
| 1 | **PostCreator** | — | `posts-topic` | `post.Id` | N threads, batched sends |
| 2a | **PostPersister** | `posts-topic` | Cosmos DB `Posts` | `/id` | Reads raw posts and inserts them into the database for lookup |
| 2b | **HashTagCounter** | `posts-topic` | `hashtags-topic` | `hashtag` string | 1 consumer per partition; in-memory aggregation; buffer swap every ~100 posts |
| 3 | **HashTagPersister** | `hashtags-topic` | Cosmos DB `Hashtags` | `/hashtag` | Additive upsert; at-least-once safe (idempotent merge) |
| 4 | **UserView** | Cosmos DB | HTTP clients | — | Read-only query API for trending hashtags and post metadata |

## Key Design Points

- **Partition alignment** — `posts-topic` uses `postId` for even distribution; `hashtags-topic` uses the hashtag string so all counts for the same hashtag land on the same partition → single-writer per hashtag in the persister.
- **Buffer-swap aggregation** — HashTagCounter accumulates `{hashtag → count}` in memory. Every ~100 posts it atomically swaps to a fresh buffer and flushes the old one as a batch of `{hashtag, count, timestamp}` messages. This reduces downstream event volume by orders of magnitude.
- **At-least-once / additive merge** — HashTagPersister reads a `{hashtag, count}` message and does `totalPostCount += count` via read-modify-write upsert. Duplicate deliveries may inflate counts slightly; acceptable for a POC (exactly-once requires conditional writes with ETags).
- **Checkpoint after write** — Both consumers checkpoint to Blob Storage only after successful downstream write, ensuring no data loss on crash.
