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

        var queryVector = await _embeddingProvider.CreateEmbeddingAsync(BuildEmbeddingQuery(query.Query), cancellationToken).ConfigureAwait(false);
        var queryIntent = DetectEntryPointIntent(query.Query);
        var searchRequest = new VectorSearchRequest
        {
            QueryVector = queryVector,
            TopK = queryIntent
                ? Math.Max(Math.Max(query.TopK * 10, _options.RerankWindowSize * 4), 100)
                : Math.Max(query.TopK * 5, _options.RerankWindowSize),
            RepoName = query.RepoName,
            Filters = query.Filters,
        };

        var candidates = await _vectorStore.SearchAsync(searchRequest, cancellationToken).ConfigureAwait(false);
        var filtered = candidates.Where(candidate => MatchesFilters(candidate.Payload, query.RepoName, query.Filters)).ToList();
        var reranked = filtered
            .Select(candidate => ScoreCandidate(query.Query, candidate))
            .OrderByDescending(candidate => candidate.Score)
            .Take(query.TopK)
            .ToList();

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
        var symbolKind = ParseSymbolKind(symbolKindText);
        var startLine = GetInt(payload, "startLine");
        var endLine = GetInt(payload, "endLine");
        var relatedSymbols = dependencies;
        var routeTemplate = GetString(payload, "routeTemplate");
        var httpVerb = GetString(payload, "httpVerb");
        var controllerName = GetString(payload, "controllerName");
        var projectName = GetString(payload, "projectName");
        var normalizedQuery = Normalize(query);
        var queryIntent = DetectEntryPointIntent(query);

        var queryTokens = Tokenize(query);
        var symbolTokens = Tokenize(symbolName);
        var summaryTokens = Tokenize(summary);
        var signatureTokens = Tokenize(signature);
        var fileTokens = Tokenize(filePath);
        var chunkTokens = Tokenize(chunkText);
        var dependencyTokens = Tokenize(string.Join(' ', dependencies));
        var routeTokens = Tokenize(routeTemplate);
        var controllerTokens = Tokenize(controllerName);
        var projectTokens = Tokenize(projectName);
        var fileNameTokens = Tokenize(Path.GetFileNameWithoutExtension(filePath));

        var score = candidate.Score;
        score += ComputeKeywordBoost(normalizedQuery, queryTokens, queryIntent, symbolKind, symbolName, symbolTokens, signature, signatureTokens, summary, summaryTokens, filePath, fileTokens, chunkTokens, dependencyTokens, routeTemplate, routeTokens, httpVerb, controllerName, controllerTokens, projectName, projectTokens, fileNameTokens);

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

    private double ComputeKeywordBoost(
        string normalizedQuery,
        HashSet<string> queryTokens,
        bool queryIntent,
        CodeSymbolKind symbolKind,
        string symbolName,
        HashSet<string> symbolTokens,
        string signature,
        HashSet<string> signatureTokens,
        string summary,
        HashSet<string> summaryTokens,
        string filePath,
        HashSet<string> fileTokens,
        HashSet<string> chunkTokens,
        HashSet<string> dependencyTokens,
        string routeTemplate,
        HashSet<string> routeTokens,
        string httpVerb,
        string controllerName,
        HashSet<string> controllerTokens,
        string projectName,
        HashSet<string> projectTokens,
        HashSet<string> fileNameTokens)
    {
        var boost = 0.0;
        var maxBoost = Math.Max(0, _options.KeywordBoostMax);

        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return 0;
        }

        AddMatch(string.Equals(symbolName, normalizedQuery, StringComparison.OrdinalIgnoreCase), 1.0);
        AddMatch(string.Equals(Path.GetFileNameWithoutExtension(filePath), normalizedQuery, StringComparison.OrdinalIgnoreCase), 0.55);
        AddMatch(!string.IsNullOrWhiteSpace(signature) && Normalize(signature).Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase), 0.45);
        AddMatch(!string.IsNullOrWhiteSpace(summary) && Normalize(summary).Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase), 0.28);
        AddMatch(!string.IsNullOrWhiteSpace(routeTemplate) && Normalize(routeTemplate).Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase), 0.32);
        AddMatch(!string.IsNullOrWhiteSpace(httpVerb) && normalizedQuery.Contains(httpVerb, StringComparison.OrdinalIgnoreCase), 0.14);
        AddMatch(!string.IsNullOrWhiteSpace(controllerName) && normalizedQuery.Contains(NormalizeControllerName(controllerName), StringComparison.OrdinalIgnoreCase), 0.34);

        AddTokenOverlap(queryTokens, symbolTokens, 0.26);
        AddTokenOverlap(queryTokens, signatureTokens, 0.18);
        AddTokenOverlap(queryTokens, summaryTokens, 0.16);
        AddTokenOverlap(queryTokens, fileTokens, 0.09);
        AddTokenOverlap(queryTokens, chunkTokens, 0.06);
        AddTokenOverlap(queryTokens, dependencyTokens, 0.08);
        AddTokenOverlap(queryTokens, routeTokens, 0.14);
        AddTokenOverlap(queryTokens, controllerTokens, 0.12);
        AddTokenOverlap(queryTokens, projectTokens, 0.05);
        AddTokenOverlap(queryTokens, fileNameTokens, 0.11);

        if (symbolKind == CodeSymbolKind.ControllerAction)
        {
            boost += 0.28;
        }

        if (queryIntent)
        {
            if (symbolKind == CodeSymbolKind.ControllerAction)
            {
                boost += 0.85;
            }

            if (IsControllerOrServicePath(filePath))
            {
                boost += 0.24;
            }

            if (!string.IsNullOrWhiteSpace(routeTemplate))
            {
                boost += 0.22;
            }

            if (!string.IsNullOrWhiteSpace(controllerName))
            {
                boost += 0.18;
            }

            if (IsRepositoryPath(filePath))
            {
                boost -= 0.18;
            }
        }

        if (queryTokens.Contains("product") && !queryTokens.Contains("category"))
        {
            var candidateMentionsProduct = symbolTokens.Contains("product")
                || routeTokens.Contains("product")
                || summaryTokens.Contains("product")
                || fileNameTokens.Contains("product");

            if (!candidateMentionsProduct && symbolKind == CodeSymbolKind.ControllerAction)
            {
                boost -= 0.28;
            }
        }

        if (queryTokens.Contains("controller") && !string.IsNullOrWhiteSpace(routeTemplate))
        {
            boost += 0.22;
        }

        if (queryTokens.Contains("product") && (symbolTokens.Contains("product") || routeTokens.Contains("product")))
        {
            boost += 0.18;
        }

        if (queryTokens.Contains("product") && queryTokens.Contains("search"))
        {
            if (symbolTokens.Contains("searchproducts") || Normalize(routeTemplate).Contains("products/search", StringComparison.OrdinalIgnoreCase))
            {
                boost += 1.05;
            }
        }

        if (queryTokens.Contains("action") && !string.IsNullOrWhiteSpace(httpVerb))
        {
            boost += 0.12;
        }

        if (queryTokens.Contains("validation") && summaryTokens.Contains("validate"))
        {
            boost += 0.14;
        }

        return Math.Min(boost, maxBoost);

        void AddMatch(bool match, double value)
        {
            if (match)
            {
                boost += value;
            }
        }

        void AddTokenOverlap(HashSet<string> left, HashSet<string> right, double value)
        {
            if (left.Count > 0 && right.Count > 0 && left.Overlaps(right))
            {
                boost += value;
            }
        }
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString().Trim();
    }

    private static string NormalizeControllerName(string value)
    {
        var normalized = Normalize(value);
        return normalized.EndsWith("controller", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^"controller".Length].Trim()
            : normalized;
    }

    private static bool DetectEntryPointIntent(string query)
    {
        var tokens = Tokenize(query);
        return tokens.Contains("where")
            || tokens.Contains("handled")
            || tokens.Contains("handling")
            || tokens.Contains("entry")
            || tokens.Contains("endpoint")
            || tokens.Contains("controller")
            || tokens.Contains("route")
            || tokens.Contains("action");
    }

    private static string BuildEmbeddingQuery(string query)
    {
        if (!DetectEntryPointIntent(query))
        {
            return query;
        }

        return string.Join(
            '\n',
            query,
            "controller action route service endpoint handled api",
            "entry point search");
    }

    private static bool IsControllerOrServicePath(string filePath)
    {
        return filePath.Contains(@"\Controllers\", StringComparison.OrdinalIgnoreCase)
            || filePath.Contains(@"\ControllerNanoServices\", StringComparison.OrdinalIgnoreCase)
            || filePath.Contains(@"\Services\", StringComparison.OrdinalIgnoreCase)
            || filePath.Contains(@"/Controllers/", StringComparison.OrdinalIgnoreCase)
            || filePath.Contains(@"/ControllerNanoServices/", StringComparison.OrdinalIgnoreCase)
            || filePath.Contains(@"/Services/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRepositoryPath(string filePath)
    {
        return filePath.Contains(@"\Repository\", StringComparison.OrdinalIgnoreCase)
            || filePath.Contains(@"\SqlRepository\", StringComparison.OrdinalIgnoreCase)
            || filePath.Contains(@"/Repository/", StringComparison.OrdinalIgnoreCase)
            || filePath.Contains(@"/SqlRepository/", StringComparison.OrdinalIgnoreCase);
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
