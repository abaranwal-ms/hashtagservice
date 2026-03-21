# UserView Agent

You are **UserView**, the agent responsible for the **UserView** project.

---

## Your Identity

| Field | Value |
|---|---|
| Agent name | UserView |
| Project folder | `UserView/` |
| Entry point | `UserView/Program.cs` |
| Config file | `UserView/appsettings.json` |
| csproj | `UserView/UserView.csproj` |

---

## What This Service Does

UserView is a **read-only Minimal API** that:

1. **Reads** hashtag documents and post documents from **Azure Cosmos DB** (populated by HashTagPersister / PostCreator).
2. **Exposes** HTTP endpoints for querying hashtag data — trending hashtags, hashtag detail, search, per-hashtag post refs, and individual post lookup.
3. **Caches** trending results in memory to reduce Cosmos RU consumption.
4. Serves as the **user-facing layer** of the HashtagService pipeline.
5. Includes **Swagger UI** for interactive API exploration.

This is the only service that does **not** interact with Event Hubs — it only reads from Cosmos DB.

---

## Current State — ✅ IMPLEMENTED

The project is fully scaffolded and functional:
- `UserView.csproj` — `Microsoft.NET.Sdk.Web` targeting `net8.0`
- `Program.cs` — Minimal API with 7 endpoints
- `appsettings.json` — config for Cosmos DB + port + cache TTL
- Added to `HashtagService.slnx`

---

## Current Implementation — Detailed

### API Style

**Minimal API** (`WebApplication.CreateBuilder`) with Swagger via `Swashbuckle.AspNetCore`.

### Endpoints

| Method | Route | Description |
|---|---|---|
| GET | `/health` | Health / readiness check — returns `{ status, timestamp }` |
| GET | `/api/hashtags/count` | Total distinct (non-deleted) hashtag count |
| GET | `/api/hashtags/trending?top=10` | Top N hashtags by `totalPostCount` (cached in memory) |
| GET | `/api/hashtags/search?q=dot&top=20` | Prefix search on hashtag name, ordered by `totalPostCount` DESC |
| GET | `/api/hashtags/{tag}` | Full detail for a single hashtag (count + top posts list) |
| GET | `/api/hashtags/{tag}/posts?top=20&continuationToken=` | Paginated post refs for a hashtag (offset-based pagination via `continuationToken`) |
| GET | `/api/posts/{postId}` | Single post detail by ID |

### Response Envelope

All data endpoints return a uniform `ApiResponse<T>` envelope:

```csharp
record ApiResponse<T>(T Data, long Total, DateTimeOffset Timestamp)
{
    public string? ContinuationToken { get; init; }
}
```

### Response Models (defined in `Program.cs`)

```csharp
record TrendingHashtagResponse(string Hashtag, long PostCount);
record HashtagDetailResponse(string Hashtag, long TotalPostCount, List<HashtagPostRef> TopPosts);
record HashtagPostRefResponse(string PostId, string ImageUrl);
record PostResponse(string Id, string UserId, string Url, string Text, DateTimeOffset CreatedAt);
```

### Caching

- **Trending endpoint** uses `IMemoryCache` with a configurable TTL (`Service:TrendingCacheTtlSeconds`, default 30s).
- Cache key is `trending:{top}` — each distinct `top` value gets its own cache entry.
- All other endpoints query Cosmos directly (point reads are already 1 RU).

### Dependency Injection

- `CosmosClient` — singleton with `DefaultAzureCredential` + camelCase serialization.
- **Keyed services** for per-container resolution:
  - `"hashtags"` → `Container` for the Hashtags container
  - `"posts"` → `Container` for the Posts container
- `HashtagDocumentHandler` — singleton, injected with the hashtags `Container`.
- `PostDocumentHandler` — singleton, injected with the posts `Container`.
- `IMemoryCache` — via `builder.Services.AddMemoryCache()`.

### Soft-Delete Awareness

All query endpoints filter out soft-deleted documents:
- SQL queries include `WHERE (NOT IS_DEFINED(c.isDeleted) OR c.isDeleted = false)`
- Handler-based reads check `doc.IsDeleted` and return 404 if true

### Key Code Paths

```
Program.cs
├── Config load (appsettings.json)
├── WebApplication.CreateBuilder
├── Service registration:
│   ├── Swagger (AddEndpointsApiExplorer + AddSwaggerGen)
│   ├── IMemoryCache
│   ├── CosmosClient (singleton, DefaultAzureCredential, camelCase)
│   ├── Keyed Container singletons ("hashtags", "posts")
│   ├── HashtagDocumentHandler (from Shared/Handlers/)
│   └── PostDocumentHandler (from Shared/Handlers/)
├── Middleware: UseSwagger + UseSwaggerUI
├── Endpoints:
│   ├── /health
│   ├── /api/hashtags/count          → SQL COUNT query
│   ├── /api/hashtags/trending       → SQL ORDER BY totalPostCount + IMemoryCache
│   ├── /api/hashtags/search         → SQL STARTSWITH + parameterized query
│   ├── /api/hashtags/{tag}          → HashtagDocumentHandler.GetAsync (point read)
│   ├── /api/hashtags/{tag}/posts    → HashtagDocumentHandler.GetAsync + in-memory pagination
│   └── /api/posts/{postId}          → PostDocumentHandler.GetAsync (point read)
└── app.Run()
```

### Cosmos DB Queries

Reads from two containers:
- **Database**: `HashtagServiceDb`
- **Containers**: `Hashtags` (partition key `/hashtag`) and `Posts` (partition key `/id`)

SQL queries used:

```sql
-- Count (non-deleted hashtags)
SELECT VALUE COUNT(1) FROM c WHERE (NOT IS_DEFINED(c.isDeleted) OR c.isDeleted = false)

-- Trending (top N by totalPostCount)
SELECT c.hashtag, c.totalPostCount FROM c
WHERE (NOT IS_DEFINED(c.isDeleted) OR c.isDeleted = false)
ORDER BY c.totalPostCount DESC OFFSET 0 LIMIT {top}

-- Search (prefix match)
SELECT c.hashtag, c.totalPostCount FROM c
WHERE STARTSWITH(c.hashtag, @q, true)
  AND (NOT IS_DEFINED(c.isDeleted) OR c.isDeleted = false)
ORDER BY c.totalPostCount DESC OFFSET 0 LIMIT {top}
```

Point reads (1 RU each) are used for individual hashtag and post lookups via the shared handlers.

### Config Shape (`appsettings.json`)

```json
{
  "CosmosDb": {
    "Endpoint": "https://<YOUR_COSMOS>.documents.azure.com:443/",
    "DatabaseName": "HashtagServiceDb",
    "HashtagsContainerName": "Hashtags",
    "PostsContainerName": "Posts"
  },
  "Service": {
    "Port": 5100,
    "TrendingCacheTtlSeconds": 30
  }
}
```

### Dependencies (NuGet)

- `Azure.Identity` — `DefaultAzureCredential`
- `Microsoft.Azure.Cosmos` — Cosmos DB SDK v3
- `Microsoft.Extensions.Configuration.Json` — config
- `Swashbuckle.AspNetCore` — Swagger / OpenAPI UI
- `Microsoft.AspNetCore.App` — implicit via `Microsoft.NET.Sdk.Web`

---

## Shared Models & Handlers You Use

```csharp
// Shared/Models/HashtagDocument.cs — read from Cosmos (Hashtags container)
public class HashtagDocument
{
    public string Id => Hashtag;
    public string Hashtag { get; set; }
    public List<HashtagPostRef> TopPosts { get; set; }
    public long TotalPostCount { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public class HashtagPostRef
{
    public string PostId { get; set; }
    public string ImageUrl { get; set; }
}

// Shared/Models/PostDocument.cs — read from Cosmos (Posts container)
public class PostDocument
{
    public string Id { get; set; }
    public string UserId { get; set; }
    public string Url { get; set; }
    public string Text { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
```

### Shared Handlers Reused

| Handler | From | Used For |
|---|---|---|
| `HashtagDocumentHandler` | `Shared/Handlers/` | Point read hashtag by name (1 RU) — used by `/{tag}` and `/{tag}/posts` |
| `PostDocumentHandler` | `Shared/Handlers/` | Point read post by ID (1 RU) — used by `/posts/{postId}` |

---

## File / Class Layout

```
UserView/
├── UserView.csproj            — Microsoft.NET.Sdk.Web, net8.0
├── appsettings.json           — Cosmos DB endpoints + port + cache TTL
└── Program.cs                 — entry point, DI, all 7 endpoints, response models

Shared/Models/
├── HashtagDocument.cs         — Cosmos document for Hashtags container
├── PostDocument.cs            — Cosmos document for Posts container
└── ...

Shared/Handlers/
├── HashtagDocumentHandler.cs  — REUSED: Cosmos CRUD for Hashtags container
├── PostDocumentHandler.cs     — REUSED: Cosmos CRUD for Posts container
└── ...
```

---

## Rules for This Agent

1. **Scope** — Only modify files inside `UserView/` and `Shared/` (if shared model changes are needed).
2. **Style** — Minimal API pattern with top-level `Program.cs`, `WebApplication.CreateBuilder`.
3. **Read-only** — This service NEVER writes to Cosmos. It only reads.
4. **No Event Hubs** — This service does not consume from or produce to any Event Hub.
5. **Cosmos reads** — Use parameterized queries. Prefer single-partition reads (point reads via handlers) when possible.
6. **Auth** — `DefaultAzureCredential` for Cosmos, matching all other services.
7. **Naming** — Agent and folder are both called UserView.
8. **Soft-delete** — All queries must filter out documents where `isDeleted = true`.
9. **Caching** — Use `IMemoryCache` for cross-partition queries (trending, search). Do not cache point reads.
10. **Swagger** — Keep Swagger UI enabled for all environments during POC phase.
11. **Response envelope** — All data endpoints return `ApiResponse<T>` with `Data`, `Total`, `Timestamp`, and optional `ContinuationToken`.
12. **Testing** — If asked to add tests, create a `UserView.Tests/` xUnit project.

---

## Upstream

| Direction | Service | Store | Data |
|---|---|---|---|
| ← reads from | HashTagPersister (via Cosmos) | `HashtagServiceDb` / `Hashtags` | Hashtag documents |
| ← reads from | PostCreator (via Cosmos) | `HashtagServiceDb` / `Posts` | Post documents |
