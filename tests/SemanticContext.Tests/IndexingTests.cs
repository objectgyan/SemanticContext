using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SemanticContext.Contracts;
using SemanticContext.Infrastructure;
using SemanticContext.Indexer;

namespace SemanticContext.Tests;

public sealed class IndexingTests
{
    [Fact]
    public async Task Indexer_extracts_expected_semantic_units_and_lines()
    {
        var tempCache = CreateTempDirectory();
        var store = new InMemoryVectorStore();
        var indexer = CreateIndexer(store, tempCache);

        var result = await indexer.IndexAsync(new IndexRequest
        {
            SolutionPath = FixturePaths.TinySolutionPath,
            RepoName = "TinySolution",
            CommitSha = "abc123",
            ReindexMode = ReindexMode.Full,
        });

        Assert.True(result.Status == IndexStatus.Completed, string.Join(Environment.NewLine, result.Errors));
        Assert.Empty(result.Errors);
        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(1, result.FilesIndexed);
        Assert.Equal(9, result.ChunksCreated);
        Assert.Empty(result.Errors);

        var chunks = store.Records.ToDictionary(record => GetString(record.Payload, "symbolName") + "|" + GetString(record.Payload, "symbolKind") + "|" + GetString(record.Payload, "containingType"));
        Assert.Contains("IOrderService|Interface|", chunks);
        Assert.Contains("Order|Record|", chunks);
        Assert.Contains("OrderService|Class|", chunks);
        Assert.Contains("SourceName|Property|OrderService", chunks);
        Assert.Contains("GetOrderAsync|Method|IOrderService", chunks);
        Assert.Contains("GetOrderAsync|Method|OrderService", chunks);
        Assert.Contains("OrdersController|Class|", chunks);
        Assert.Contains(chunks, pair => pair.Key.EndsWith("|Constructor|OrdersController", StringComparison.Ordinal));
        Assert.Contains("GetOrderAsync|ControllerAction|OrdersController", chunks);

        Assert.Equal(14, GetInt(chunks["SourceName|Property|OrderService"].Payload, "startLine"));
        Assert.Equal(14, GetInt(chunks["SourceName|Property|OrderService"].Payload, "endLine"));

        Assert.Equal(7, GetInt(chunks["GetOrderAsync|Method|IOrderService"].Payload, "startLine"));
        Assert.Equal(7, GetInt(chunks["GetOrderAsync|Method|IOrderService"].Payload, "endLine"));

        Assert.Equal(16, GetInt(chunks["GetOrderAsync|Method|OrderService"].Payload, "startLine"));
        Assert.Equal(19, GetInt(chunks["GetOrderAsync|Method|OrderService"].Payload, "endLine"));

        var constructorChunk = Assert.Single(chunks, pair => pair.Key.EndsWith("|Constructor|OrdersController", StringComparison.Ordinal));
        Assert.Equal(28, GetInt(constructorChunk.Value.Payload, "startLine"));
        Assert.Equal(31, GetInt(constructorChunk.Value.Payload, "endLine"));

        Assert.Equal(33, GetInt(chunks["GetOrderAsync|ControllerAction|OrdersController"].Payload, "startLine"));
        Assert.Equal(38, GetInt(chunks["GetOrderAsync|ControllerAction|OrdersController"].Payload, "endLine"));
    }

    [Fact]
    public async Task Chunk_text_and_summary_are_deterministic()
    {
        var tempCache = CreateTempDirectory();
        var store = new InMemoryVectorStore();
        var indexer = CreateIndexer(store, tempCache);

        var first = await indexer.IndexAsync(new IndexRequest
        {
            SolutionPath = FixturePaths.TinySolutionPath,
            RepoName = "TinySolution",
            CommitSha = "abc123",
            ReindexMode = ReindexMode.Full,
        });

        var firstChunk = store.Records.First(record => GetString(record.Payload, "symbolName") == "GetOrderAsync" && GetString(record.Payload, "symbolKind") == "Method" && GetString(record.Payload, "containingType") == "OrderService");
        var firstChunkText = GetString(firstChunk.Payload, "chunkText");
        var firstSummary = GetString(firstChunk.Payload, "summary");
        var firstHash = GetString(firstChunk.Payload, "contentHash");

        Assert.Contains("Symbol: GetOrderAsync", firstChunkText);
        Assert.Contains("Kind: Method", firstChunkText);
        Assert.Contains("Summary:", firstChunkText);
        Assert.Contains("Code:", firstChunkText);
        Assert.False(string.IsNullOrWhiteSpace(firstSummary));
        Assert.False(string.IsNullOrWhiteSpace(firstHash));

        store = new InMemoryVectorStore();
        indexer = CreateIndexer(store, tempCache);
        var second = await indexer.IndexAsync(new IndexRequest
        {
            SolutionPath = FixturePaths.TinySolutionPath,
            RepoName = "TinySolution",
            CommitSha = "abc123",
            ReindexMode = ReindexMode.Full,
        });

        var secondChunk = store.Records.First(record => GetString(record.Payload, "symbolName") == "GetOrderAsync" && GetString(record.Payload, "symbolKind") == "Method" && GetString(record.Payload, "containingType") == "OrderService");
        Assert.Equal(firstHash, GetString(secondChunk.Payload, "contentHash"));
        Assert.Equal(firstSummary, GetString(secondChunk.Payload, "summary"));
        Assert.Equal(first.Status, second.Status);
    }

    private static SolutionCodeIndexer CreateIndexer(InMemoryVectorStore store, string cacheDirectory)
    {
        return new SolutionCodeIndexer(
            new DeterministicHashEmbeddingProvider(Options.Create(new EmbeddingProviderOptions { Dimension = 256 })),
            store,
            new DeterministicCodeSummaryGenerator(),
            new Sha256ContentHasher(),
            Options.Create(new IndexingOptions
            {
                CacheDirectory = cacheDirectory,
                SnippetLength = 220,
            }),
            NullLogger<SolutionCodeIndexer>.Instance);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "semanticcontext-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string GetString(IReadOnlyDictionary<string, object?> payload, string key)
    {
        return payload.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
    }

    private static int GetInt(IReadOnlyDictionary<string, object?> payload, string key)
    {
        return payload.TryGetValue(key, out var value) && int.TryParse(value?.ToString(), out var parsed)
            ? parsed
            : 0;
    }
}
