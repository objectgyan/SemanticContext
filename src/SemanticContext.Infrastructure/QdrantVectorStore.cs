using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SemanticContext.Contracts;
using System.Net.Http.Json;
using System.Text.Json;

namespace SemanticContext.Infrastructure;

public sealed class QdrantVectorStore : IVectorStore
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly QdrantOptions _options;
    private readonly ILogger<QdrantVectorStore> _logger;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;

    public QdrantVectorStore(
        IHttpClientFactory httpClientFactory,
        IOptions<QdrantOptions> options,
        ILogger<QdrantVectorStore> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task UpsertAsync(IReadOnlyCollection<VectorRecord> records, CancellationToken cancellationToken = default)
    {
        if (records.Count == 0)
        {
            return;
        }

        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        var points = records.Select(record => new
        {
            id = record.Id,
            vector = record.Vector,
            payload = record.Payload,
        });

        var response = await client.PutAsJsonAsync(
            $"collections/{_options.CollectionName}/points?wait=true",
            new { points },
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(VectorSearchRequest request, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        var body = new Dictionary<string, object?>
        {
            ["vector"] = request.QueryVector,
            ["limit"] = request.TopK,
            ["with_payload"] = true,
        };

        var filter = BuildFilter(request.RepoName, request.Filters);
        if (filter is not null)
        {
            body["filter"] = filter;
        }

        using var response = await client.PostAsJsonAsync(
            $"collections/{_options.CollectionName}/points/search",
            body,
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("result", out var resultArray))
        {
            return Array.Empty<VectorSearchResult>();
        }

        var results = new List<VectorSearchResult>();
        foreach (var item in resultArray.EnumerateArray())
        {
            var payload = ReadPayload(item);
            results.Add(new VectorSearchResult
            {
                Id = item.TryGetProperty("id", out var id) ? id.ToString() : string.Empty,
                Score = item.TryGetProperty("score", out var score) && score.TryGetDouble(out var parsedScore) ? parsedScore : 0,
                Payload = payload,
            });
        }

        return results;
    }

    public async Task DeleteByRepoAsync(string repoName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repoName))
        {
            return;
        }

        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        var filter = BuildFilter(repoName, null);
        using var response = await client.PostAsJsonAsync(
            $"collections/{_options.CollectionName}/points/delete?wait=true",
            new { filter },
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpClient> GetClientAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return _httpClientFactory.CreateClient("qdrant");
        }

        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_initialized)
            {
                await EnsureCollectionAsync(cancellationToken).ConfigureAwait(false);
                _initialized = true;
            }
        }
        finally
        {
            _initializeGate.Release();
        }

        return _httpClientFactory.CreateClient("qdrant");
    }

    private async Task EnsureCollectionAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("qdrant");
        using var existing = await client.GetAsync($"collections/{_options.CollectionName}", cancellationToken).ConfigureAwait(false);
        if (existing.IsSuccessStatusCode)
        {
            return;
        }

        if (existing.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            existing.EnsureSuccessStatusCode();
        }

        var createBody = new
        {
            vectors = new
            {
                size = _options.VectorSize,
                distance = "Cosine",
            },
        };

        using var createResponse = await client.PutAsJsonAsync($"collections/{_options.CollectionName}", createBody, cancellationToken).ConfigureAwait(false);
        createResponse.EnsureSuccessStatusCode();
    }

    private static object? BuildFilter(string? repoName, CodeContextFilters? filters)
    {
        var must = new List<object>();

        if (!string.IsNullOrWhiteSpace(repoName))
        {
            must.Add(new
            {
                key = "repoName",
                match = new { value = repoName },
            });
        }

        if (filters is null)
        {
            return must.Count == 0 ? null : new { must };
        }

        if (filters.ProjectNames is { Count: > 0 } projectNames)
        {
            must.Add(new
            {
                should = projectNames.Select(projectName => new
                {
                    key = "projectName",
                    match = new { value = projectName },
                }),
                min_should = 1,
            });
        }

        if (filters.FilePaths is { Count: > 0 } filePaths)
        {
            must.Add(new
            {
                should = filePaths.Select(filePath => new
                {
                    key = "filePath",
                    match = new { value = filePath },
                }),
                min_should = 1,
            });
        }

        if (filters.SymbolKinds is { Count: > 0 } symbolKinds)
        {
            must.Add(new
            {
                should = symbolKinds.Select(symbolKind => new
                {
                    key = "symbolKind",
                    match = new { value = symbolKind.ToString() },
                }),
                min_should = 1,
            });
        }

        if (filters.Attributes is { Count: > 0 } attributes)
        {
            must.Add(new
            {
                should = attributes.Select(attribute => new
                {
                    key = "attributes",
                    match = new { value = attribute },
                }),
                min_should = 1,
            });
        }

        return must.Count == 0 ? null : new { must };
    }

    private static IReadOnlyDictionary<string, object?> ReadPayload(JsonElement item)
    {
        if (!item.TryGetProperty("payload", out var payloadElement) || payloadElement.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, object?>();
        }

        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in payloadElement.EnumerateObject())
        {
            payload[property.Name] = ReadJsonValue(property.Value);
        }

        return payload;
    }

    private static object? ReadJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt32(out var intValue) => intValue,
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => element.EnumerateArray().Select(ReadJsonValue).ToArray(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.ToString(),
        };
    }
}

