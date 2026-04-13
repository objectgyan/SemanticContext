using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SemanticContext.Contracts;
using SemanticContext.Infrastructure;

namespace SemanticContext.Tests;

public sealed class QdrantIntegrationTests
{
    [Fact]
    public async Task Qdrant_vector_store_can_upsert_search_and_delete_against_a_real_container()
    {
        var qdrantUrl = Environment.GetEnvironmentVariable("SEMANTICCONTEXT_QDRANT_URL") ?? "http://localhost:6333";
        if (!await IsQdrantReachableAsync(qdrantUrl))
        {
            return;
        }

        var collectionName = $"semanticcontext-tests-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddHttpClient("qdrant", client => client.BaseAddress = new Uri(qdrantUrl, UriKind.Absolute));
        services.AddSingleton(Options.Create(new QdrantOptions
        {
            Url = qdrantUrl,
            CollectionName = collectionName,
            VectorSize = 4,
        }));
        services.AddSingleton<ILogger<QdrantVectorStore>, NullLogger<QdrantVectorStore>>();
        services.AddSingleton<IVectorStore, QdrantVectorStore>();

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IVectorStore>();

        var vector = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };
        var record = new VectorRecord
        {
            Id = "repo:sample",
            Vector = vector,
            Payload = new Dictionary<string, object?>
            {
                ["repoName"] = "sample",
                ["projectName"] = "Sample.Project",
                ["filePath"] = "src/Sample.cs",
                ["symbolKind"] = CodeSymbolKind.Method.ToString(),
                ["symbolName"] = "GetOrderAsync",
                ["attributes"] = Array.Empty<string>(),
                ["dependencies"] = Array.Empty<string>(),
                ["summary"] = "Loads order data.",
                ["chunkText"] = "GetOrderAsync loads order data.",
            },
        };

        await store.UpsertAsync([record]);
        var results = await store.SearchAsync(new VectorSearchRequest
        {
            QueryVector = vector,
            TopK = 5,
            RepoName = "sample",
        });

        Assert.NotEmpty(results);
        Assert.Equal("repo:sample", results[0].Id);

        await store.DeleteByIdsAsync([record.Id]);
        var afterDelete = await store.SearchAsync(new VectorSearchRequest
        {
            QueryVector = vector,
            TopK = 5,
            RepoName = "sample",
        });

        Assert.Empty(afterDelete);
    }

    private static async Task<bool> IsQdrantReachableAsync(string url)
    {
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(url, UriKind.Absolute), Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.GetAsync("/collections", HttpCompletionOption.ResponseHeadersRead);
            return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound;
        }
        catch
        {
            return false;
        }
    }
}
