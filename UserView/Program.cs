using Azure.Identity;
using HashtagService.Shared.Handlers;
using HashtagService.Shared.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Caching.Memory;

// ---------------------------------------------------------------------------
// Bootstrap
// ---------------------------------------------------------------------------
var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.json", optional: false);

// ---------------------------------------------------------------------------
// Config
// ---------------------------------------------------------------------------
var cosmosEndpoint   = builder.Configuration["CosmosDb:Endpoint"]!;
var databaseName     = builder.Configuration["CosmosDb:DatabaseName"]!;
var hashtagsContainer = builder.Configuration["CosmosDb:HashtagsContainerName"]!;
var postsContainer   = builder.Configuration["CosmosDb:PostsContainerName"]!;
var port             = builder.Configuration.GetValue<int>("Service:Port", 5100);
var cacheTtlSeconds  = builder.Configuration.GetValue<int>("Service:TrendingCacheTtlSeconds", 30);

builder.WebHost.UseUrls($"http://+:{port}");

// ---------------------------------------------------------------------------
// Services
// ---------------------------------------------------------------------------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddMemoryCache();

// CosmosClient (singleton)
builder.Services.AddSingleton<CosmosClient>(_ =>
    new CosmosClient(cosmosEndpoint, new DefaultAzureCredential(), new CosmosClientOptions
    {
        SerializerOptions = new CosmosSerializationOptions
        {
            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
        }
    }));

// Per-container singletons
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<CosmosClient>().GetContainer(databaseName, hashtagsContainer));

// Named containers via keyed services so both handler types resolve the right container
builder.Services.AddKeyedSingleton<Container>("hashtags", (sp, _) =>
    sp.GetRequiredService<CosmosClient>().GetContainer(databaseName, hashtagsContainer));

builder.Services.AddKeyedSingleton<Container>("posts", (sp, _) =>
    sp.GetRequiredService<CosmosClient>().GetContainer(databaseName, postsContainer));

builder.Services.AddSingleton<HashtagDocumentHandler>(sp =>
    new HashtagDocumentHandler(sp.GetRequiredKeyedService<Container>("hashtags")));

builder.Services.AddSingleton<PostDocumentHandler>(sp =>
    new PostDocumentHandler(sp.GetRequiredKeyedService<Container>("posts")));

// ---------------------------------------------------------------------------
// Build
// ---------------------------------------------------------------------------
var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

// ---------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------

// GET /health
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    timestamp = DateTimeOffset.UtcNow
}));

// GET /api/hashtags/count
app.MapGet("/api/hashtags/count", async (
    [FromKeyedServices("hashtags")] Container container,
    CancellationToken ct) =>
{
    const string sql = "SELECT VALUE COUNT(1) FROM c WHERE (NOT IS_DEFINED(c.isDeleted) OR c.isDeleted = false)";
    var query = new QueryDefinition(sql);
    using var iterator = container.GetItemQueryIterator<long>(query);
    var page = await iterator.ReadNextAsync(ct);
    var count = page.FirstOrDefault();
    return Results.Ok(new ApiResponse<long>(count, 1, DateTimeOffset.UtcNow));
});

// GET /api/hashtags/trending?top=10
app.MapGet("/api/hashtags/trending", async (
    IMemoryCache cache,
    [FromKeyedServices("hashtags")] Container container,
    int top = 10,
    CancellationToken ct = default) =>
{
    if (top <= 0) top = 10;
    var cacheKey = $"trending:{top}";

    if (!cache.TryGetValue(cacheKey, out List<TrendingHashtagResponse>? cached))
    {
        var sql = $"SELECT c.hashtag, c.totalPostCount FROM c WHERE (NOT IS_DEFINED(c.isDeleted) OR c.isDeleted = false) ORDER BY c.totalPostCount DESC OFFSET 0 LIMIT {top}";
        var query = new QueryDefinition(sql);
        using var iterator = container.GetItemQueryIterator<TrendingHashtagResponse>(query);

        var docs = new List<TrendingHashtagResponse>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            docs.AddRange(page);
        }

        cached = docs;

        cache.Set(cacheKey, cached, TimeSpan.FromSeconds(cacheTtlSeconds));
    }

    return Results.Ok(new ApiResponse<List<TrendingHashtagResponse>>(cached!, cached!.Count, DateTimeOffset.UtcNow));
});

// GET /api/hashtags/search?q=dot&top=20
app.MapGet("/api/hashtags/search", async (
    [FromKeyedServices("hashtags")] Container container,
    string q,
    int top = 20,
    CancellationToken ct = default) =>
{
    if (top <= 0) top = 20;

    var sql = $"""
        SELECT c.hashtag, c.totalPostCount FROM c
        WHERE STARTSWITH(c.hashtag, @q, true)
          AND (NOT IS_DEFINED(c.isDeleted) OR c.isDeleted = false)
        ORDER BY c.totalPostCount DESC
        OFFSET 0 LIMIT {top}
        """;

    var query = new QueryDefinition(sql).WithParameter("@q", q);
    using var iterator = container.GetItemQueryIterator<TrendingHashtagResponse>(query);

    var results = new List<TrendingHashtagResponse>();
    while (iterator.HasMoreResults)
    {
        var page = await iterator.ReadNextAsync(ct);
        results.AddRange(page);
    }

    return Results.Ok(new ApiResponse<List<TrendingHashtagResponse>>(results, results.Count, DateTimeOffset.UtcNow));
});

// GET /api/hashtags/{tag}
app.MapGet("/api/hashtags/{tag}", async (
    string tag,
    HashtagDocumentHandler handler,
    CancellationToken ct) =>
{
    var doc = await handler.GetAsync(tag, ct);
    if (doc is null || doc.IsDeleted)
        return Results.NotFound();

    var response = new HashtagDetailResponse(doc.Hashtag, doc.TotalPostCount, doc.TopPosts);
    return Results.Ok(new ApiResponse<HashtagDetailResponse>(response, 1, DateTimeOffset.UtcNow));
});

// GET /api/hashtags/{tag}/posts?top=20&continuationToken=
app.MapGet("/api/hashtags/{tag}/posts", async (
    string tag,
    HashtagDocumentHandler handler,
    int top = 20,
    string? continuationToken = null,
    CancellationToken ct = default) =>
{
    if (top <= 0) top = 20;

    var doc = await handler.GetAsync(tag, ct);
    if (doc is null || doc.IsDeleted)
        return Results.NotFound();

    // Parse numeric offset from continuationToken
    int skip = 0;
    if (!string.IsNullOrEmpty(continuationToken) && int.TryParse(continuationToken, out var parsed))
        skip = parsed;

    var page = doc.TopPosts
        .Skip(skip)
        .Take(top)
        .Select(p => new HashtagPostRefResponse(p.PostId, p.ImageUrl))
        .ToList();

    var nextOffset = skip + page.Count;
    var nextToken = nextOffset < doc.TopPosts.Count ? nextOffset.ToString() : null;

    return Results.Ok(new ApiResponse<List<HashtagPostRefResponse>>(page, doc.TopPosts.Count, DateTimeOffset.UtcNow)
        with { ContinuationToken = nextToken });
});

// GET /api/posts/{postId}
app.MapGet("/api/posts/{postId}", async (
    string postId,
    PostDocumentHandler handler,
    CancellationToken ct) =>
{
    var doc = await handler.GetAsync(postId, ct);
    if (doc is null || doc.IsDeleted)
        return Results.NotFound();

    var response = new PostResponse(doc.Id, doc.UserId, doc.Url, doc.Text, doc.CreatedAt);
    return Results.Ok(new ApiResponse<PostResponse>(response, 1, DateTimeOffset.UtcNow));
});

app.Run();

// ---------------------------------------------------------------------------
// Response models
// ---------------------------------------------------------------------------
record ApiResponse<T>(T Data, long Total, DateTimeOffset Timestamp)
{
    public string? ContinuationToken { get; init; }
}

record TrendingHashtagResponse(string Hashtag, long PostCount);
record HashtagDetailResponse(string Hashtag, long TotalPostCount, List<HashtagPostRef> TopPosts);
record HashtagPostRefResponse(string PostId, string ImageUrl);
record PostResponse(string Id, string UserId, string Url, string Text, DateTimeOffset CreatedAt);
