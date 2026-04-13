using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SemanticContext.Contracts;
using SemanticContext.Infrastructure;
using SemanticContext.Indexer;
using SemanticContext.Retrieval;

namespace SemanticContext.Tests;

public sealed class RetrievalTests
{
    [Fact]
    public async Task Filters_narrow_results_by_project_and_symbol_kind()
    {
        var store = new InMemoryVectorStore();
        var embedding = new DeterministicHashEmbeddingProvider(Options.Create(new EmbeddingProviderOptions { Dimension = 256 }));
        await SeedRecordAsync(store, embedding, "TinySolution", "TinySolution", "GetOrderAsync", CodeSymbolKind.Method, "Task<Order> GetOrderAsync(int id, CancellationToken ct)", "Order service method.");
        await SeedRecordAsync(store, embedding, "OtherRepo", "OtherProject", "NoiseType", CodeSymbolKind.Class, "public class NoiseType", "Noise.");

        var retriever = CreateRetriever(store);
        var response = await retriever.QueryAsync(new CodeContextQuery
        {
            Query = "GetOrderAsync",
            RepoName = "TinySolution",
            TopK = 10,
            Filters = new CodeContextFilters
            {
                ProjectNames = ["TinySolution"],
                SymbolKinds = [CodeSymbolKind.Method],
            },
        });

        Assert.Single(response.Results);
        Assert.Equal("GetOrderAsync", response.Results[0].SymbolName);
        Assert.Equal(CodeSymbolKind.Method, response.Results[0].SymbolKind);
    }

    [Fact]
    public async Task Exact_symbol_match_is_ranked_first()
    {
        var store = new InMemoryVectorStore();
        var embedding = new DeterministicHashEmbeddingProvider(Options.Create(new EmbeddingProviderOptions { Dimension = 256 }));
        await SeedRecordAsync(store, embedding, "TinySolution", "TinySolution", "GetOrderAsync", CodeSymbolKind.Method, "Task<Order> GetOrderAsync(int id, CancellationToken ct)", "Order service method.");
        await SeedRecordAsync(store, embedding, "TinySolution", "TinySolution", "OrderLookup", CodeSymbolKind.Method, "Task<Order> OrderLookup(int id)", "Method that looks up an order.");

        var retriever = CreateRetriever(store);
        var response = await retriever.QueryAsync(new CodeContextQuery
        {
            Query = "GetOrderAsync",
            RepoName = "TinySolution",
            TopK = 5,
            Filters = new CodeContextFilters
            {
                SymbolKinds = [CodeSymbolKind.Method],
            },
        });

        Assert.NotEmpty(response.Results);
        Assert.Equal("GetOrderAsync", response.Results[0].SymbolName);
    }

    [Fact]
    public async Task End_to_end_index_and_query_returns_relevant_results()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "semanticcontext-tests", Guid.NewGuid().ToString("N"));
        var store = new InMemoryVectorStore();
        var indexer = new SolutionCodeIndexer(
            new DeterministicHashEmbeddingProvider(Options.Create(new EmbeddingProviderOptions { Dimension = 256 })),
            store,
            new DeterministicCodeSummaryGenerator(),
            new Sha256ContentHasher(),
            Options.Create(new IndexingOptions
            {
                CacheDirectory = cacheDir,
                SnippetLength = 220,
            }),
            NullLogger<SolutionCodeIndexer>.Instance);

        await indexer.IndexAsync(new IndexRequest
        {
            SolutionPath = FixturePaths.TinySolutionPath,
            RepoName = "TinySolution",
            CommitSha = "abc123",
            ReindexMode = ReindexMode.Full,
        });

        var retriever = CreateRetriever(store);
        var response = await retriever.QueryAsync(new CodeContextQuery
        {
            Query = "order service",
            RepoName = "TinySolution",
            TopK = 5,
            Filters = new CodeContextFilters
            {
                ProjectNames = ["TinySolution"],
                SymbolKinds = [CodeSymbolKind.Method],
            },
        });

        Assert.NotEmpty(response.Results);
        Assert.Equal("GetOrderAsync", response.Results[0].SymbolName);
        Assert.False(string.IsNullOrWhiteSpace(response.Results[0].Summary));
    }

    private static VectorStoreCodeContextRetriever CreateRetriever(InMemoryVectorStore store)
    {
        return new VectorStoreCodeContextRetriever(
            new DeterministicHashEmbeddingProvider(Options.Create(new EmbeddingProviderOptions { Dimension = 256 })),
            store,
            Options.Create(new RetrievalOptions { RerankWindowSize = 25, KeywordBoostMax = 12 }),
            NullLogger<VectorStoreCodeContextRetriever>.Instance);
    }

    private static async Task SeedRecordAsync(
        InMemoryVectorStore store,
        DeterministicHashEmbeddingProvider embedding,
        string repoName,
        string projectName,
        string symbolName,
        CodeSymbolKind symbolKind,
        string signature,
        string summary)
    {
        var input = string.Join('\n', symbolName, signature, summary, "snippet");
        var vector = await embedding.CreateEmbeddingAsync(input);
        await store.UpsertAsync(new[]
        {
            new VectorRecord
            {
                Id = $"{repoName}:{symbolName}:{symbolKind}",
                Vector = vector,
                Payload = new Dictionary<string, object?>
                {
                    ["repoName"] = repoName,
                    ["projectName"] = projectName,
                    ["filePath"] = $"src/{projectName}/{symbolName}.cs",
                    ["symbolId"] = symbolName,
                    ["symbolKind"] = symbolKind.ToString(),
                    ["symbolName"] = symbolName,
                    ["signature"] = signature,
                    ["summary"] = summary,
                    ["chunkText"] = $"{symbolName} {signature} {summary}",
                    ["snippet"] = "snippet",
                    ["attributes"] = Array.Empty<string>(),
                    ["dependencies"] = Array.Empty<string>(),
                },
            },
        });
    }
}

