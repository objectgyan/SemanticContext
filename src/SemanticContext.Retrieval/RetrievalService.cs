using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SemanticContext.Contracts;
using System.Globalization;
using System.Text;

namespace SemanticContext.Retrieval;

public sealed class VectorStoreCodeContextRetriever : ICodeContextRetriever
{
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IVectorStore _vectorStore;
    private readonly RetrievalOptions _options;
    private readonly ILogger<VectorStoreCodeContextRetriever> _logger;

    public VectorStoreCodeContextRetriever(
        IEmbeddingProvider embeddingProvider,
        IVectorStore vectorStore,
        IOptions<RetrievalOptions> options,
        ILogger<VectorStoreCodeContextRetriever> logger)
    {
        _embeddingProvider = embeddingProvider;
        _vectorStore = vectorStore;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<CodeContextResponse> QueryAsync(CodeContextQuery query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
        {
            return new CodeContextResponse
            {
                Query = query.Query,
                Results = Array.Empty<CodeContextResult>(),
            };
        }

        var queryVector = await _embeddingProvider.CreateEmbeddingAsync(query.Query, cancellationToken).ConfigureAwait(false);
        var searchRequest = new VectorSearchRequest
        {
            QueryVector = queryVector,
            TopK = Math.Max(query.TopK * 5, _options.RerankWindowSize),
            RepoName = query.RepoName,
            Filters = query.Filters,
        };

        var candidates = await _vectorStore.SearchAsync(searchRequest, cancellationToken).ConfigureAwait(false);
        var filtered = candidates.Where(candidate => MatchesFilters(candidate.Payload, query.RepoName, query.Filters)).ToList();
        var reranked = filtered.Select(candidate => ScoreCandidate(query.Query, candidate)).OrderByDescending(candidate => candidate.Score).Take(query.TopK).ToList();

        return new CodeContextResponse
        {
            Query = query.Query,
            Results = reranked.Select(candidate => candidate.Result).ToList(),
        };
    }

    private CandidateScore ScoreCandidate(string query, VectorSearchResult candidate)
    {
        var payload = candidate.Payload;
        var symbolName = GetString(payload, "symbolName");
        var signature = GetString(payload, "signature");
        var summary = GetString(payload, "summary");
        var filePath = GetString(payload, "filePath");
        var chunkText = GetString(payload, "chunkText");
        var dependencies = GetStringList(payload, "dependencies");
        var symbolKindText = GetString(payload, "symbolKind");
        var startLine = GetInt(payload, "startLine");
        var endLine = GetInt(payload, "endLine");
        var relatedSymbols = dependencies;

        var queryTokens = Tokenize(query);
        var symbolTokens = Tokenize(symbolName);
        var summaryTokens = Tokenize(summary);
        var signatureTokens = Tokenize(signature);
        var fileTokens = Tokenize(filePath);
        var chunkTokens = Tokenize(chunkText);

        var score = candidate.Score;
        if (!string.IsNullOrWhiteSpace(symbolName) && string.Equals(symbolName, query, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.6;
        }

        if (!string.IsNullOrWhiteSpace(symbolName) && queryTokens.Overlaps(symbolTokens))
        {
            score += 0.25;
        }

        if (queryTokens.Overlaps(signatureTokens))
        {
            score += 0.15;
        }

        if (queryTokens.Overlaps(summaryTokens))
        {
            score += 0.12;
        }

        if (queryTokens.Overlaps(fileTokens))
        {
            score += 0.08;
        }

        if (queryTokens.Overlaps(chunkTokens))
        {
            score += 0.05;
        }

        if (dependencies.Count > 0 && queryTokens.Overlaps(Tokenize(string.Join(' ', dependencies))))
        {
            score += 0.04;
        }

        if (!string.IsNullOrWhiteSpace(signature) &&
            query.Contains(signature, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.35;
        }

        var snippet = GetString(payload, "snippet");
        if (string.IsNullOrWhiteSpace(snippet))
        {
            snippet = chunkText;
        }

        var result = new CodeContextResult
        {
            Score = Math.Round(score, 4),
            ProjectName = GetString(payload, "projectName"),
            FilePath = filePath,
            SymbolName = symbolName,
            SymbolKind = ParseSymbolKind(symbolKindText),
            Signature = signature,
            Summary = summary,
            Snippet = snippet,
            StartLine = startLine,
            EndLine = endLine,
            RelatedSymbols = relatedSymbols,
            RouteTemplate = GetString(payload, "routeTemplate"),
            HttpVerb = GetString(payload, "httpVerb"),
            ControllerName = GetString(payload, "controllerName"),
            IsApiController = GetBool(payload, "isApiController"),
        };

        return new CandidateScore(result, score);
    }

    private static bool MatchesFilters(IReadOnlyDictionary<string, object?> payload, string repoName, CodeContextFilters? filters)
    {
        if (!string.IsNullOrWhiteSpace(repoName) &&
            !string.Equals(GetString(payload, "repoName"), repoName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (filters is null)
        {
            return true;
        }

        if (filters.ProjectNames is { Count: > 0 } projectNames)
        {
            var projectName = GetString(payload, "projectName");
            if (!projectNames.Any(name => string.Equals(name, projectName, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        if (filters.FilePaths is { Count: > 0 } filePaths)
        {
            var filePath = GetString(payload, "filePath");
            if (!filePaths.Any(path => string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        if (filters.SymbolKinds is { Count: > 0 } symbolKinds)
        {
            var symbolKind = ParseSymbolKind(GetString(payload, "symbolKind"));
            var symbolKindMatch = symbolKinds.Any(filter => SymbolKindMatches(symbolKind, filter));
            if (!symbolKindMatch)
            {
                return false;
            }
        }

        if (filters.Attributes is { Count: > 0 } attributes)
        {
            var payloadAttributes = GetStringList(payload, "attributes");
            if (!attributes.All(attribute => payloadAttributes.Any(item => string.Equals(item, attribute, StringComparison.OrdinalIgnoreCase))))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SymbolKindMatches(CodeSymbolKind actual, CodeSymbolKind filter)
    {
        if (filter == CodeSymbolKind.NamedType)
        {
            return actual is CodeSymbolKind.Class or CodeSymbolKind.Interface or CodeSymbolKind.Record;
        }

        return actual == filter;
    }

    private static CodeSymbolKind ParseSymbolKind(string? value)
    {
        return Enum.TryParse<CodeSymbolKind>(value, ignoreCase: true, out var parsed)
            ? parsed
            : CodeSymbolKind.NamedType;
    }

    private static string GetString(IReadOnlyDictionary<string, object?> payload, string key)
    {
        return payload.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
    }

    private static int GetInt(IReadOnlyDictionary<string, object?> payload, string key)
    {
        return payload.TryGetValue(key, out var value) && int.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static bool GetBool(IReadOnlyDictionary<string, object?> payload, string key)
    {
        return payload.TryGetValue(key, out var value) && bool.TryParse(value?.ToString(), out var parsed)
            ? parsed
            : false;
    }

    private static IReadOnlyList<string> GetStringList(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
        {
            return Array.Empty<string>();
        }

        if (value is IEnumerable<string> strings)
        {
            return strings.ToArray();
        }

        if (value is IEnumerable<object> objects)
        {
            return objects.Select(item => item?.ToString() ?? string.Empty).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
        }

        return value.ToString() is string text && !string.IsNullOrWhiteSpace(text)
            ? text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();
    }

    private static HashSet<string> Tokenize(string? value)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
        {
            return tokens;
        }

        var builder = new StringBuilder();
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (builder.Length > 0)
            {
                tokens.Add(builder.ToString());
                builder.Clear();
            }
        }
    }

    private sealed record CandidateScore(CodeContextResult Result, double Score);
}

public static class RetrievalRegistration
{
    public static IServiceCollection AddSemanticContextRetrieval(this IServiceCollection services)
    {
        services.AddScoped<ICodeContextRetriever, VectorStoreCodeContextRetriever>();
        return services;
    }
}
