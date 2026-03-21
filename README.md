# HashtagService

> Real-time hashtag trending pipeline — .NET 8 · Azure Event Hubs · Cosmos DB

![Hashtag Posts Table UI](./docs/images/HashTag-Posts.png)

```
PostGenerator ──► posts-topic ──► HashtagExtractor ──► hashtags-topic ──► HashtagPersister ──► Cosmos DB
   (fake posts)                    (extract + count)                       (merge upsert)        ▲
                                                                                                 │
                                                                                            UserView API
```

**[Full architecture →](docs/architecture.md)**

## Projects

| Folder | What it does |
|---|---|
| `PostGenerator/` | Multi-threaded fake post producer → Event Hubs + Cosmos |
| `HashtagExtractor/` | Consumes posts, extracts hashtags, aggregates counts in a double-buffer, publishes to `hashtags-topic` |
| `HashtagPersister/` | Consumes hashtag counts, merge-upserts into Cosmos DB |
| `UserView/` | Read-only Minimal API — trending hashtags, search, post lookup (Swagger UI on `:5100`) |
| `Shared/` | Models + Cosmos/Event Hub handlers |

## Quick Start

**Prerequisites:** [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) · [Azure CLI](https://aka.ms/installazurecli) · [PowerShell 7+](https://aka.ms/install-powershell)

```bash
# 1. Login
az login
az account set --subscription 67e53100-61d9-49b5-8176-ad06015325bf

# 2. Deploy infra (one-time, ~2 min each, idempotent)
pwsh infra/kafka/deploy.ps1    # Event Hubs + Blob checkpoints
pwsh infra/cosmos/deploy.ps1   # Cosmos DB serverless

# 3. Build
dotnet build HashtagService.slnx

# 4. Run (4 terminals — start consumers first, producer last)
dotnet run --project HashtagPersister     # Terminal 1
dotnet run --project HashtagExtractor     # Terminal 2
dotnet run --project UserView             # Terminal 3  (http://localhost:5100)
dotnet run --project PostGenerator        # Terminal 4
```

Ctrl+C to stop — each service flushes and checkpoints on shutdown.

**Verify trending hashtags:**
```bash
# Via UserView API
curl http://localhost:5100/api/hashtags/trending?top=5

# Or via Azure CLI
az cosmosdb sql query -a hashtagservice-cosmos -d HashtagServiceDb -c Hashtags \
  -q "SELECT TOP 5 c.hashtag, c.totalPostCount FROM c ORDER BY c.totalPostCount DESC" \
  -g hashtagservice
```

## Running Each Service

### Startup Order

Start services **bottom-up** (consumers before producers) so no messages are missed:

1. **HashtagPersister** — consumes from `hashtags-topic`, writes to Cosmos DB
2. **HashtagExtractor** — consumes from `posts-topic`, publishes to `hashtags-topic`
3. **UserView** — reads from Cosmos DB, serves API on `http://localhost:5100`
4. **PostGenerator** — produces fake posts to `posts-topic` + writes to Cosmos `Posts`

> UserView can be started at any point — it only reads from Cosmos and has no Event Hub dependency.

### Per-Service Commands

```bash
# HashtagPersister
dotnet run --project HashtagPersister
# Reads: hashtags-topic | Writes: Cosmos DB (Hashtags container)
# Config: HashtagPersister/appsettings.json

# HashtagExtractor
dotnet run --project HashtagExtractor
# Reads: posts-topic + Cosmos DB (Posts container) | Writes: hashtags-topic
# Config: HashtagExtractor/appsettings.json

# UserView (Minimal API with Swagger)
dotnet run --project UserView
# Reads: Cosmos DB (Hashtags + Posts containers)
# Browse: http://localhost:5100/swagger for interactive docs
# Config: UserView/appsettings.json

# PostGenerator
dotnet run --project PostGenerator
# Writes: posts-topic + Cosmos DB (Posts container)
# Config: PostGenerator/appsettings.json
```

### UserView API Quick Reference

| Endpoint | Description |
|---|---|
| `GET /health` | Health check |
| `GET /api/hashtags/trending?top=10` | Top N trending hashtags |
| `GET /api/hashtags/search?q=tech&top=20` | Search hashtags by prefix |
| `GET /api/hashtags/count` | Total distinct hashtag count |
| `GET /api/hashtags/{tag}` | Detail for a single hashtag |
| `GET /api/hashtags/{tag}/posts?top=20` | Post refs for a hashtag (paginated) |
| `GET /api/posts/{postId}` | Single post detail |

### Stopping Services

Press **Ctrl+C** in each terminal. All services handle `SIGINT` gracefully:
- **PostGenerator** — stops producing, exits cleanly
- **HashtagExtractor** — flushes remaining hashtag counts, checkpoints, exits
- **HashtagPersister** — checkpoints current offset, exits
- **UserView** — shuts down the Kestrel web server

## Cost

| Duration | Cost |
|---|---|
| 1 day | **~$0.84** |
| 4 days | **~$3.36** |
| 30 days | **~$25** |

> 87% of cost is the Event Hubs Throughput Unit ($22/mo, always-on). Cosmos DB serverless is pennies at dev scale. **Delete when idle:**

```bash
az group delete -n hashtagservice --yes --no-wait   # nuke everything
# re-run deploy scripts to recreate — Bicep is idempotent
```

## Copilot Agents

This repo ships with agent prompts for GitHub Copilot — use `@workspace` with:

`#PostCreator` · `#HashTagCounter` · `#HashTagPersister` · `#UserView` · `#Database` · `#Kafka`
