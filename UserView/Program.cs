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

// Cosmos containers + handlers
builder.Services.AddKeyedSingleton<Container>("hashtags", (sp, _) =>
    sp.GetRequiredService<CosmosClient>().GetContainer(databaseName, hashtagsContainer));

builder.Services.AddSingleton<HashtagDocumentHandler>(sp =>
    new HashtagDocumentHandler(sp.GetRequiredKeyedService<Container>("hashtags")));

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
