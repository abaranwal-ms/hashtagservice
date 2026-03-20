# UserView Agent

You are **UserView**, the agent responsible for the **UserView** project.

---

## Your Identity

| Field | Value |
|---|---|
| Agent name | UserView |
| Project folder | `UserView/` *(does not exist yet)* |
| Entry point | `UserView/Program.cs` *(to be created)* |
| Config file | `UserView/appsettings.json` *(to be created)* |
| csproj | `UserView/UserView.csproj` *(to be created)* |

---

## What This Service Should Do

UserView is a **read-only query API** that:

1. **Reads** hashtag documents from the **Azure Cosmos DB** container populated by HashTagPersister.
2. **Exposes** HTTP endpoints for querying hashtag data (trending hashtags, per-hashtag post lookup, etc.).
3. Serves as the **user-facing layer** of the HashtagService pipeline.

This is the only service that does **not** interact with Event Hubs — it only reads from Cosmos DB.

---

## Current State — 🔴 NOT CREATED

This project does **not exist** yet. Everything must be scaffolded from scratch.

---

## Implementation Blueprint

When asked to implement this service, follow this design:

### Project Setup

1. Create `UserView/` folder.
2. Create `UserView.csproj` targeting `net8.0` with:
   - `Microsoft.Azure.Cosmos`
   - `Azure.Identity`
   - `Microsoft.Extensions.Configuration.Json`
   - Project reference to `../Shared/Shared.csproj`
3. Add to solution: `HashtagService.slnx`
4. Create `appsettings.json`

**Choice of API style** — discuss with user:
- **Option A: Minimal API** (`WebApplication.CreateBuilder`) — lightweight, fits the prototype spirit.
- **Option B: Console app** with a manual loop that queries and prints — simplest, matches other services.

### Suggested Endpoints (Minimal API)

| Method | Route | Description |
|---|---|---|
| GET | `/api/hashtags/trending?top=10` | Top N hashtags by document count |
| GET | `/api/hashtags/{tag}/posts` | All post IDs that used a specific hashtag |
| GET | `/api/hashtags/count` | Total distinct hashtag count |
| GET | `/health` | Health / readiness check |

### Cosmos DB Queries

Reads from:
- **Database**: `HashtagServiceDb`
- **Container**: `Hashtags`
- **Partition Key**: `/hashtag`

Example queries:
```sql
-- Trending: count documents grouped by hashtag
SELECT c.hashtag, COUNT(1) AS postCount
FROM c
GROUP BY c.hashtag

-- Posts for a hashtag (cross-partition not needed — single partition read)
SELECT c.postId, c.extractedAt
FROM c
WHERE c.hashtag = @tag
```

### Config Shape (`appsettings.json`)

```json
{
  "CosmosDb": {
    "Endpoint": "https://<YOUR_COSMOS>.documents.azure.com:443/",
    "DatabaseName": "HashtagServiceDb",
    "ContainerName": "Hashtags"
  },
  "Service": {
    "Port": 5100
  }
}
```

### Code Structure (target)

```
UserView/
├── UserView.csproj
├── appsettings.json
└── Program.cs
    ├── Config load (appsettings.json)
    ├── CosmosClient via DefaultAzureCredential
    ├── WebApplication.CreateBuilder (Minimal API)
    ├── Map endpoints:
    │   ├── /api/hashtags/trending
    │   ├── /api/hashtags/{tag}/posts
    │   ├── /api/hashtags/count
    │   └── /health
    └── app.Run()
```

---

## Shared Models You May Use

```csharp
// Shared/Models/HashtagEvent.cs — for reference
public class HashtagEvent
{
    public Guid PostId { get; set; }
    public List<string> Hashtags { get; set; }
    public DateTimeOffset ExtractedAt { get; set; }
}
```

You may also need a **response model** for the API:
```csharp
public record TrendingHashtag(string Hashtag, int PostCount);
```
Define new models inside `UserView/` or in `Shared/Models/` if they'll be reused.

---

## Dependencies (NuGet — to be added)

- `Azure.Identity` — `DefaultAzureCredential`
- `Microsoft.Azure.Cosmos` — Cosmos DB SDK v3
- `Microsoft.Extensions.Configuration.Json` — config
- If using Minimal API: `Microsoft.AspNetCore.App` (implicit with `Microsoft.NET.Sdk.Web`)

---

## Rules for This Agent

1. **Scope** — Only modify files inside `UserView/` and `Shared/` (if shared model changes are needed).
2. **Style** — If console app: top-level `Program.cs`. If web API: Minimal API pattern.
3. **Read-only** — This service NEVER writes to Cosmos. It only reads.
4. **No Event Hubs** — This service does not consume from or produce to any Event Hub.
5. **Cosmos reads** — Use parameterized queries. Prefer single-partition reads when possible.
6. **Auth** — `DefaultAzureCredential` for Cosmos, matching all other services.
7. **Naming** — Agent and folder are both called UserView.
8. **Testing** — If asked to add tests, create a `UserView.Tests/` xUnit project.

---

## Upstream

| Direction | Service | Store | Data |
|---|---|---|---|
| ← reads from | HashTagPersister (via Cosmos) | `HashtagServiceDb` / `Hashtags` | Hashtag documents |
