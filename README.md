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

# 4. Run (3 terminals — start consumers first)
dotnet run --project HashtagPersister     # Terminal 1
dotnet run --project HashtagExtractor     # Terminal 2
dotnet run --project PostGenerator        # Terminal 3
```

Ctrl+C to stop — each service flushes and checkpoints on shutdown.

**Verify trending hashtags:**
```bash
az cosmosdb sql query -a hashtagservice-cosmos -d HashtagServiceDb -c Hashtags \
  -q "SELECT TOP 5 c.hashtag, c.totalPostCount FROM c ORDER BY c.totalPostCount DESC" \
  -g hashtagservice
```

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

`#PostCreator` · `#HashTagCounter` · `#HashTagPersister` · `#Database` · `#Kafka`
