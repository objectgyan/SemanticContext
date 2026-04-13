using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SemanticContext.Contracts;

namespace SemanticContext.Infrastructure;

public sealed class RemoteHttpEmbeddingProvider : IEmbeddingProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly EmbeddingProviderOptions _options;
    private readonly ILogger<RemoteHttpEmbeddingProvider> _logger;

    public RemoteHttpEmbeddingProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<EmbeddingProviderOptions> options,
        ILogger<RemoteHttpEmbeddingProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<float>> CreateEmbeddingAsync(string input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.EndpointUrl))
        {
            throw new InvalidOperationException("EmbeddingProvider:EndpointUrl must be configured for the remote embedding provider.");
        }

        var client = _httpClientFactory.CreateClient("embedding");
        using var response = await client.PostAsJsonAsync(
            "embeddings",
            new
            {
                input,
                model = _options.Model,
            },
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var embedding = ReadEmbedding(document.RootElement);
        if (embedding.Count == 0)
        {
            _logger.LogWarning("Remote embedding provider returned an empty embedding.");
        }

        return embedding;
    }

    private static IReadOnlyList<float> ReadEmbedding(JsonElement root)
    {
        if (TryReadArray(root, "embedding", out var embedding))
        {
            return embedding;
        }

        if (TryReadArray(root, "vector", out embedding))
        {
            return embedding;
        }

        if (root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Array &&
            data.GetArrayLength() > 0 &&
            TryReadArray(data[0], "embedding", out embedding))
        {
            return embedding;
        }

        return Array.Empty<float>();
    }

    private static bool TryReadArray(JsonElement element, string propertyName, out IReadOnlyList<float> values)
    {
        values = Array.Empty<float>();
        if (!element.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var list = new List<float>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.TryGetSingle(out var value))
            {
                list.Add(value);
            }
            else if (item.TryGetDouble(out var doubleValue))
            {
                list.Add((float)doubleValue);
            }
        }

        values = list;
        return values.Count > 0;
    }
}
