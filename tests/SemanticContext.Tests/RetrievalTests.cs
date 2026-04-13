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
    public async Task Controller_action_metadata_boosts_route_level_matches()
    {
        var store = new InMemoryVectorStore();
        var embedding = new DeterministicHashEmbeddingProvider(Options.Create(new EmbeddingProviderOptions { Dimension = 256 }));

        await SeedRecordAsync(
            store,
            embedding,
            "TinySolution",
            "TinySolution.Api",
            "ValidateOrderAsync",
            CodeSymbolKind.ControllerAction,
            "Task<IActionResult> ValidateOrderAsync(Guid id, CancellationToken ct)",
            "Validates an order before checkout.",
            routeTemplate: "api/orders/{id}/validate",
            httpVerb: "POST",
            controllerName: "OrdersController",
            isApiController: true);

        await SeedRecordAsync(
            store,
            embedding,
            "TinySolution",
            "TinySolution.Core",
            "ValidateOrderAsync",
            CodeSymbolKind.Method,
            "Task<ValidationResult> ValidateOrderAsync(Order order, CancellationToken ct)",
            "Validates an order aggregate and returns validation details.");

        var retriever = CreateRetriever(store);
        var response = await retriever.QueryAsync(new CodeContextQuery
        {
            Query = "where is the order validation before checkout",
            RepoName = "TinySolution",
            TopK = 5,
            Filters = new CodeContextFilters
            {
                SymbolKinds = [CodeSymbolKind.ControllerAction, CodeSymbolKind.Method],
            },
        });

        Assert.NotEmpty(response.Results);
        Assert.Equal(CodeSymbolKind.ControllerAction, response.Results[0].SymbolKind);
        Assert.Equal("OrdersController", response.Results[0].ControllerName);
        Assert.Equal("api/orders/{id}/validate", response.Results[0].RouteTemplate);
    }

    [Fact]
    public async Task Entry_point_queries_prefer_controller_actions_over_repository_helpers()
    {
        var store = new InMemoryVectorStore();
        var embedding = new DeterministicHashEmbeddingProvider(Options.Create(new EmbeddingProviderOptions { Dimension = 256 }));

        await SeedRecordAsync(
            store,
            embedding,
            "TinySolution",
            "TinySolution.Api",
            "SearchProducts",
            CodeSymbolKind.ControllerAction,
            "ActionResult<List<SearchProductsResponse>> SearchProducts(SearchProductsRequest args)",
            "ASP.NET RevitController action that handles route api/v1/revit/products/search.",
            routeTemplate: "api/v1/revit/products/search",
            httpVerb: "HttpPost",
            controllerName: "RevitController",
            isApiController: true);

        await SeedRecordAsync(
            store,
            embedding,
            "TinySolution",
            "TinySolution.Repository",
            "Product_Search",
            CodeSymbolKind.Method,
            "PaginationSqlResult<ProductSearchResult> Product_Search(ProductSearchArgs args)",
            "Repository method that searches products in SQL.");

        var retriever = CreateRetriever(store);
        var response = await retriever.QueryAsync(new CodeContextQuery
        {
            Query = "where is product search handled",
            RepoName = "TinySolution",
            TopK = 5,
            Filters = new CodeContextFilters
            {
                SymbolKinds = [CodeSymbolKind.ControllerAction, CodeSymbolKind.Method],
            },
        });

        Assert.NotEmpty(response.Results);
        Assert.Equal("SearchProducts", response.Results[0].SymbolName);
        Assert.Equal(CodeSymbolKind.ControllerAction, response.Results[0].SymbolKind);
    }

    [Fact]
    public async Task How_style_queries_prefer_exact_route_matches_over_broader_actions()
    {
        var searchProducts = new VectorSearchResult
        {
            Id = "search-exact",
            Score = 0.71,
            Payload = new Dictionary<string, object?>
            {
                ["repoName"] = "TinySolution",
                ["projectName"] = "TinySolution.Api",
                ["filePath"] = @"C:\\Projects\\TinySolution\\ApiSite\\Controllers\\Items\\_SearchItems.cs",
                ["symbolId"] = "SearchItems",
                ["symbolKind"] = CodeSymbolKind.ControllerAction.ToString(),
                ["symbolName"] = "SearchItems",
                ["signature"] = "ActionResult<List<SearchItemsResponse>> SearchItems(SearchItemsRequest args)",
                ["summary"] = "ASP.NET ItemsController action that handles route api/items/search.",
                ["chunkText"] = "SearchItems ActionResult<List<SearchItemsResponse>> SearchItems(SearchItemsRequest args) ASP.NET ItemsController action that handles route api/items/search.",
                ["snippet"] = "[HttpPost(\"products/search\")]",
                ["attributes"] = Array.Empty<string>(),
                ["dependencies"] = Array.Empty<string>(),
                ["routeTemplate"] = "api/items/search",
                ["httpVerb"] = "HttpPost",
                ["controllerName"] = "ItemsController",
                ["isApiController"] = true,
            },
        };

        var searchByDivision = new VectorSearchResult
        {
            Id = "search-broader",
            Score = 0.92,
            Payload = new Dictionary<string, object?>
            {
                ["repoName"] = "TinySolution",
                ["projectName"] = "TinySolution.Api",
                ["filePath"] = @"C:\\Projects\\TinySolution\\ApiSite\\Controllers\\Items\\_SearchItemsByCategory.cs",
                ["symbolId"] = "SearchItemsByCategory",
                ["symbolKind"] = CodeSymbolKind.ControllerAction.ToString(),
                ["symbolName"] = "SearchItemsByCategory",
                ["signature"] = "ActionResult<List<SearchItemsResponse>> SearchItemsByCategory(SearchItemsByCategoryRequest args)",
                ["summary"] = "ASP.NET ItemsController action that handles route api/items/search-by-category.",
                ["chunkText"] = "SearchItemsByCategory ActionResult<List<SearchItemsResponse>> SearchItemsByCategory(SearchItemsByCategoryRequest args) ASP.NET ItemsController action that handles route api/items/search-by-category.",
                ["snippet"] = "[HttpGet(\"items/search-by-category\")]",
                ["attributes"] = Array.Empty<string>(),
                ["dependencies"] = Array.Empty<string>(),
                ["routeTemplate"] = "api/items/search-by-category",
                ["httpVerb"] = "HttpGet",
                ["controllerName"] = "ItemsController",
                ["isApiController"] = true,
            },
        };

        var store = new StaticVectorStore([searchByDivision, searchProducts]);
        var retriever = CreateRetriever(store);
        var response = await retriever.QueryAsync(new CodeContextQuery
        {
            Query = "how is search handled",
            RepoName = "TinySolution",
            TopK = 5,
            Filters = new CodeContextFilters
            {
                SymbolKinds = [CodeSymbolKind.ControllerAction],
            },
        });

        Assert.NotEmpty(response.Results);
        Assert.Equal("SearchItems", response.Results[0].SymbolName);
        Assert.Equal("api/items/search", response.Results[0].RouteTemplate);
    }

    [Fact]
    public async Task Architecture_queries_prefer_repository_overview_context()
    {
        var codeCandidate = new VectorSearchResult
        {
            Id = "controller-action",
            Score = 0.94,
            Payload = new Dictionary<string, object?>
            {
                ["repoName"] = "TinySolution",
                ["projectName"] = "TinySolution.Api",
                ["filePath"] = @"C:\\Projects\\TinySolution\\ApiSite\\Controllers\\Orders\\_GetOrder.cs",
                ["symbolId"] = "GetOrder",
                ["symbolKind"] = CodeSymbolKind.ControllerAction.ToString(),
                ["symbolName"] = "GetOrder",
                ["signature"] = "Task<IActionResult> GetOrder(Guid id, CancellationToken ct)",
                ["summary"] = "Controller action that returns one order.",
                ["chunkText"] = "GetOrder Task<IActionResult> GetOrder(Guid id, CancellationToken ct) Controller action that returns one order.",
                ["snippet"] = "[HttpGet(\"api/orders/{id}\")]",
                ["attributes"] = Array.Empty<string>(),
                ["dependencies"] = Array.Empty<string>(),
                ["routeTemplate"] = "api/orders/{id}",
                ["httpVerb"] = "HttpGet",
                ["controllerName"] = "OrdersController",
                ["isApiController"] = true,
            },
        };

        var catalog = new RecordingIndexCatalog
        {
            RepositoryMetadataToReturn = new RepositoryMetadata
            {
                RepoName = "TinySolution",
                DocumentCount = 12,
                ChunkCount = 40,
                ProjectCount = 3,
                ProjectNames = ["TinySolution.Api", "TinySolution.Core", "TinySolution.Infrastructure"],
                SymbolKinds = ["Class", "Method", "ControllerAction"],
            },
            ProjectMetadataToReturn =
            [
                new ProjectMetadata
                {
                    RepoName = "TinySolution",
                    ProjectName = "TinySolution.Api",
                    DocumentCount = 4,
                    ChunkCount = 16,
                    SymbolKinds = ["ControllerAction", "Class"],
                },
            ],
        };

        var store = new StaticVectorStore([codeCandidate]);
        var retriever = CreateRetriever(store, catalog);
        var response = await retriever.QueryAsync(new CodeContextQuery
        {
            Query = "what is the current architecture",
            RepoName = "TinySolution",
            TopK = 5,
        });

        Assert.NotEmpty(response.Results);
        Assert.Equal("TinySolution Architecture", response.Results[0].SymbolName);
        Assert.Contains("projects", response.Results[0].Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(response.Results, result => result.SymbolName == "TinySolution.Api Overview");
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

    private static VectorStoreCodeContextRetriever CreateRetriever(IVectorStore store, IIndexCatalog? catalog = null)
    {
        return new VectorStoreCodeContextRetriever(
            new DeterministicHashEmbeddingProvider(Options.Create(new EmbeddingProviderOptions { Dimension = 256 })),
            store,
            catalog ?? new RecordingIndexCatalog(),
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
        string summary,
        string? routeTemplate = null,
        string? httpVerb = null,
        string? controllerName = null,
        bool isApiController = false)
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
                    ["routeTemplate"] = routeTemplate,
                    ["httpVerb"] = httpVerb,
                    ["controllerName"] = controllerName,
                    ["isApiController"] = isApiController,
                },
            },
        });
    }
}
