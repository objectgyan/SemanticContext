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
    private readonly IIndexCatalog _indexCatalog;
    private readonly RetrievalOptions _options;
    private readonly ILogger<VectorStoreCodeContextRetriever> _logger;

    public VectorStoreCodeContextRetriever(
        IEmbeddingProvider embeddingProvider,
        IVectorStore vectorStore,
        IIndexCatalog indexCatalog,
        IOptions<RetrievalOptions> options,
        ILogger<VectorStoreCodeContextRetriever> logger)
    {
        _embeddingProvider = embeddingProvider;
        _vectorStore = vectorStore;
        _indexCatalog = indexCatalog;
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

        var entryPointIntent = DetectEntryPointIntent(query.Query);
        var architectureIntent = DetectArchitectureIntent(query.Query);
        var broadIntent = entryPointIntent || architectureIntent;
        var searchProbes = BuildSearchProbes(query.Query, entryPointIntent, architectureIntent);
        var searchRequest = new VectorSearchRequest
        {
            QueryVector = Array.Empty<float>(),
            TopK = broadIntent
                ? Math.Max(Math.Max(query.TopK * 10, _options.RerankWindowSize * 4), 100)
                : Math.Max(query.TopK * 5, _options.RerankWindowSize),
            RepoName = query.RepoName,
            Filters = query.Filters,
        };

        var candidates = new List<VectorSearchResult>();
        foreach (var probe in searchProbes)
        {
            searchRequest = searchRequest with
            {
                QueryVector = await _embeddingProvider.CreateEmbeddingAsync(probe, cancellationToken).ConfigureAwait(false),
            };

            var probeCandidates = await _vectorStore.SearchAsync(searchRequest, cancellationToken).ConfigureAwait(false);
            candidates.AddRange(probeCandidates);
        }

        if (architectureIntent && !string.IsNullOrWhiteSpace(query.RepoName))
        {
            var catalogCandidates = await BuildCatalogCandidatesAsync(query.RepoName, cancellationToken).ConfigureAwait(false);
            candidates.AddRange(catalogCandidates);
        }

        var merged = MergeCandidates(candidates);
        var filtered = merged
            .Where(candidate => MatchesFilters(candidate.Payload, query.RepoName, query.Filters))
            .Select(candidate => (candidate, lexicalScore: ComputeLexicalPreRank(query.Query, candidate)))
            .OrderByDescending(candidate => candidate.lexicalScore)
            .ThenByDescending(candidate => candidate.candidate.Score)
            .Take(Math.Max(query.TopK * 10, _options.RerankWindowSize))
            .Select(candidate => candidate.candidate)
            .ToList();
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
        var architectureIntent = DetectArchitectureIntent(query);

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
        var contextType = GetString(payload, "contextType");
        var contextTypeTokens = Tokenize(contextType);

        var score = candidate.Score;
        score += ComputeKeywordBoost(normalizedQuery, queryTokens, queryIntent, architectureIntent, symbolKind, symbolName, symbolTokens, signature, signatureTokens, summary, summaryTokens, filePath, fileTokens, chunkTokens, dependencyTokens, routeTemplate, routeTokens, httpVerb, controllerName, controllerTokens, projectName, projectTokens, fileNameTokens, contextType, contextTypeTokens);

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

    private static IReadOnlyList<string> BuildSearchProbes(string query, bool entryPointIntent, bool architectureIntent)
    {
        var probes = new List<string> { query };
        if (!entryPointIntent && !architectureIntent)
        {
            return probes;
        }

        probes.Add(BuildEmbeddingQuery(query, entryPointIntent, architectureIntent));

        var normalized = Normalize(query);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            probes.Add(normalized);
        }

        var queryTokens = Tokenize(query);
        if (queryTokens.Contains("controller") || queryTokens.Contains("handled") || queryTokens.Contains("how"))
        {
            probes.Add("controller action route service endpoint");
        }

        if (architectureIntent)
        {
            probes.Add("architecture structure projects adapters indexing retrieval pipeline");
            probes.Add("repository overview project responsibilities codebase design");
        }

        return probes
            .Where(probe => !string.IsNullOrWhiteSpace(probe))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<VectorSearchResult> MergeCandidates(IEnumerable<VectorSearchResult> candidates)
    {
        var merged = new Dictionary<string, VectorSearchResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (merged.TryGetValue(candidate.Id, out var existing))
            {
                if (candidate.Score > existing.Score)
                {
                    merged[candidate.Id] = candidate;
                }

                continue;
            }

            merged[candidate.Id] = candidate;
        }

        return merged.Values.ToList();
    }

    private static double ComputeLexicalPreRank(string query, VectorSearchResult candidate)
    {
        var payload = candidate.Payload;
        var symbolName = GetString(payload, "symbolName");
        var summary = GetString(payload, "summary");
        var filePath = GetString(payload, "filePath");
        var routeTemplate = GetString(payload, "routeTemplate");
        var httpVerb = GetString(payload, "httpVerb");
        var controllerName = GetString(payload, "controllerName");
        var symbolKind = ParseSymbolKind(GetString(payload, "symbolKind"));
        var projectName = GetString(payload, "projectName");
        var signature = GetString(payload, "signature");
        var contextType = GetString(payload, "contextType");

        var queryTokens = Tokenize(query);
        var symbolTokens = Tokenize(symbolName);
        var summaryTokens = Tokenize(summary);
        var routeTokens = Tokenize(routeTemplate);
        var fileTokens = Tokenize(filePath);
        var controllerTokens = Tokenize(controllerName);
        var fileNameTokens = Tokenize(Path.GetFileNameWithoutExtension(filePath));
        var projectTokens = Tokenize(projectName);
        var signatureTokens = Tokenize(signature);
        var contextTokens = Tokenize(contextType);
        var normalizedQuery = Normalize(query);
        var architectureIntent = DetectArchitectureIntent(query);
        var meaningfulQueryTokens = GetMeaningfulQueryTokens(queryTokens);
        var routeTerminalTokens = Tokenize(GetRouteTerminalSegment(routeTemplate));

        var score = 0.0;

        if (string.Equals(symbolName, normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            score += 2.0;
        }

        if (queryTokens.Overlaps(symbolTokens))
        {
            score += 0.6;
        }

        if (queryTokens.Overlaps(summaryTokens))
        {
            score += 0.4;
        }

        if (queryTokens.Overlaps(routeTokens))
        {
            score += 0.8;
        }

        if (queryTokens.Overlaps(fileTokens) || queryTokens.Overlaps(fileNameTokens))
        {
            score += 0.3;
        }

        if (queryTokens.Overlaps(projectTokens))
        {
            score += 0.3;
        }

        if (queryTokens.Overlaps(signatureTokens))
        {
            score += 0.25;
        }

        if (queryTokens.Overlaps(contextTokens))
        {
            score += 0.55;
        }

        if (!string.IsNullOrWhiteSpace(controllerName) && queryTokens.Overlaps(controllerTokens))
        {
            score += 0.5;
        }

        if (symbolKind == CodeSymbolKind.ControllerAction)
        {
            score += 0.7;
        }

        if (!string.IsNullOrWhiteSpace(routeTemplate) && Normalize(routeTemplate).Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            score += 1.1;
        }

        if (queryTokens.Contains("where") || queryTokens.Contains("handled"))
        {
            if (!string.IsNullOrWhiteSpace(routeTemplate) || !string.IsNullOrWhiteSpace(httpVerb))
            {
                score += 0.35;
            }
        }

        if (queryTokens.Contains("search") && !string.IsNullOrWhiteSpace(routeTemplate))
        {
            var routeText = routeTemplate.Trim();
            if (routeText.EndsWith("/search", StringComparison.OrdinalIgnoreCase)
                || routeText.EndsWith("\\search", StringComparison.OrdinalIgnoreCase)
                || string.Equals(routeText, "search", StringComparison.OrdinalIgnoreCase))
            {
                score += 1.0;
            }

            if (routeText.Contains("/search/", StringComparison.OrdinalIgnoreCase)
                || routeText.Contains("\\search\\", StringComparison.OrdinalIgnoreCase))
            {
                score += 0.25;
            }
        }

        if (meaningfulQueryTokens.Count > 0 && routeTerminalTokens.Count > 0)
        {
            if (routeTerminalTokens.SetEquals(meaningfulQueryTokens))
            {
                score += 1.2;
            }
            else if (routeTerminalTokens.IsSupersetOf(meaningfulQueryTokens))
            {
                score += 0.45;
            }
        }

        if (architectureIntent)
        {
            if (string.Equals(contextType, "repository-overview", StringComparison.OrdinalIgnoreCase))
            {
                score += 1.35;
            }

            if (string.Equals(contextType, "project-overview", StringComparison.OrdinalIgnoreCase))
            {
                score += 1.0;
            }

            if (summaryTokens.Contains("architecture") || summaryTokens.Contains("pipeline") || summaryTokens.Contains("adapter"))
            {
                score += 0.6;
            }
        }

        return score;
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
        char? previous = null;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (previous is char previousChar
                    && char.IsLetter(previousChar)
                    && char.IsLower(previousChar)
                    && char.IsLetter(ch)
                    && char.IsUpper(ch))
                {
                    Flush();
                }

                builder.Append(char.ToLowerInvariant(ch));
                previous = ch;
            }
            else
            {
                Flush();
                previous = null;
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

    private static HashSet<string> GetMeaningfulQueryTokens(HashSet<string> queryTokens)
    {
        return queryTokens
            .Where(token => !LowSignalQueryTokens.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private double ComputeKeywordBoost(
        string normalizedQuery,
        HashSet<string> queryTokens,
        bool queryIntent,
        bool architectureIntent,
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
        HashSet<string> fileNameTokens,
        string contextType,
        HashSet<string> contextTypeTokens)
    {
        var boost = 0.0;
        var maxBoost = Math.Max(0, _options.KeywordBoostMax);
        var meaningfulQueryTokens = GetMeaningfulQueryTokens(queryTokens);
        var routeTerminalTokens = Tokenize(GetRouteTerminalSegment(routeTemplate));

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
        AddTokenOverlap(queryTokens, contextTypeTokens, 0.2);

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

        if (queryTokens.Contains("controller") && !string.IsNullOrWhiteSpace(routeTemplate))
        {
            boost += 0.22;
        }

        if (queryTokens.Contains("action") && !string.IsNullOrWhiteSpace(httpVerb))
        {
            boost += 0.12;
        }

        if (queryTokens.Contains("validation") && summaryTokens.Contains("validate"))
        {
            boost += 0.14;
        }

        if (meaningfulQueryTokens.Count > 0 && routeTerminalTokens.Count > 0)
        {
            if (routeTerminalTokens.SetEquals(meaningfulQueryTokens))
            {
                boost += 1.1;
            }
            else if (routeTerminalTokens.IsSupersetOf(meaningfulQueryTokens))
            {
                boost += 0.3;
            }
        }

        if (architectureIntent)
        {
            if (string.Equals(contextType, "repository-overview", StringComparison.OrdinalIgnoreCase))
            {
                boost += 1.2;
            }

            if (string.Equals(contextType, "project-overview", StringComparison.OrdinalIgnoreCase))
            {
                boost += 0.85;
            }

            if (summaryTokens.Contains("architecture") || summaryTokens.Contains("project") || summaryTokens.Contains("pipeline"))
            {
                boost += 0.18;
            }
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

    private static string GetRouteTerminalSegment(string routeTemplate)
    {
        if (string.IsNullOrWhiteSpace(routeTemplate))
        {
            return string.Empty;
        }

        var trimmed = routeTemplate.Trim().TrimEnd('/', '\\');
        var separatorIndex = trimmed.LastIndexOfAny(['/', '\\']);
        return separatorIndex >= 0 ? trimmed[(separatorIndex + 1)..] : trimmed;
    }

    private async Task<IReadOnlyList<VectorSearchResult>> BuildCatalogCandidatesAsync(string repoName, CancellationToken cancellationToken)
    {
        var repository = await _indexCatalog.GetRepositoryMetadataAsync(repoName, cancellationToken).ConfigureAwait(false);
        var projects = await _indexCatalog.GetProjectMetadataAsync(repoName, cancellationToken).ConfigureAwait(false);
        var candidates = new List<VectorSearchResult>();

        if (repository is not null)
        {
            var summary = BuildRepositorySummary(repository);
            candidates.Add(CreateCatalogCandidate(
                id: $"catalog::{repoName}::repository",
                repoName: repoName,
                projectName: string.Empty,
                filePath: $".semanticcontext/{repoName}/repository-overview",
                symbolName: $"{repoName} Architecture",
                signature: "Repository architecture overview",
                summary: summary,
                snippet: summary,
                dependencies: repository.ProjectNames,
                contextType: "repository-overview",
                score: 0.72));
        }

        foreach (var project in projects)
        {
            var summary = BuildProjectSummary(project);
            candidates.Add(CreateCatalogCandidate(
                id: $"catalog::{repoName}::project::{project.ProjectName}",
                repoName: repoName,
                projectName: project.ProjectName,
                filePath: $".semanticcontext/{repoName}/projects/{project.ProjectName}",
                symbolName: $"{project.ProjectName} Overview",
                signature: "Project responsibility overview",
                summary: summary,
                snippet: summary,
                dependencies: project.SymbolKinds,
                contextType: "project-overview",
                score: 0.64));
        }

        return candidates;
    }

    private static VectorSearchResult CreateCatalogCandidate(
        string id,
        string repoName,
        string projectName,
        string filePath,
        string symbolName,
        string signature,
        string summary,
        string snippet,
        IReadOnlyList<string> dependencies,
        string contextType,
        double score)
    {
        return new VectorSearchResult
        {
            Id = id,
            Score = score,
            Payload = new Dictionary<string, object?>
            {
                ["repoName"] = repoName,
                ["projectName"] = projectName,
                ["filePath"] = filePath,
                ["symbolId"] = id,
                ["symbolKind"] = CodeSymbolKind.NamedType.ToString(),
                ["symbolName"] = symbolName,
                ["signature"] = signature,
                ["summary"] = summary,
                ["chunkText"] = $"{symbolName}\n{signature}\n{summary}",
                ["snippet"] = snippet,
                ["attributes"] = Array.Empty<string>(),
                ["dependencies"] = dependencies,
                ["contextType"] = contextType,
            },
        };
    }

    private static string BuildRepositorySummary(RepositoryMetadata metadata)
    {
        var projects = metadata.ProjectNames.Count == 0
            ? "no indexed projects"
            : string.Join(", ", metadata.ProjectNames);
        var symbolKinds = metadata.SymbolKinds.Count == 0
            ? "semantic units"
            : string.Join(", ", metadata.SymbolKinds.Take(6));

        return $"{metadata.RepoName} architecture overview with {metadata.ProjectCount} projects, {metadata.DocumentCount} indexed documents, and {metadata.ChunkCount} semantic chunks. Key projects: {projects}. Indexed structure includes {symbolKinds}.";
    }

    private static string BuildProjectSummary(ProjectMetadata metadata)
    {
        var symbolKinds = metadata.SymbolKinds.Count == 0
            ? "semantic units"
            : string.Join(", ", metadata.SymbolKinds.Take(6));

        return $"{metadata.ProjectName} project overview with {metadata.DocumentCount} indexed documents and {metadata.ChunkCount} semantic chunks. It contributes {symbolKinds} to the repository architecture.";
    }

    private static bool DetectEntryPointIntent(string query)
    {
        var tokens = Tokenize(query);
        return tokens.Contains("where")
            || (tokens.Contains("how") && (tokens.Contains("search") || tokens.Contains("handled") || tokens.Contains("works") || tokens.Contains("implemented")))
            || tokens.Contains("handled")
            || tokens.Contains("handling")
            || tokens.Contains("entry")
            || tokens.Contains("endpoint")
            || tokens.Contains("controller")
            || tokens.Contains("route")
            || tokens.Contains("action");
    }

    private static bool DetectArchitectureIntent(string query)
    {
        var tokens = Tokenize(query);
        return tokens.Contains("architecture")
            || tokens.Contains("structure")
            || tokens.Contains("design")
            || tokens.Contains("overview")
            || tokens.Contains("components")
            || tokens.Contains("projects")
            || tokens.Contains("pipeline")
            || tokens.Contains("system")
            || (tokens.Contains("current") && tokens.Contains("architecture"))
            || (tokens.Contains("how") && tokens.Contains("works"));
    }

    private static string BuildEmbeddingQuery(string query, bool entryPointIntent, bool architectureIntent)
    {
        if (!entryPointIntent && !architectureIntent)
        {
            return query;
        }

        var probeLines = new List<string> { query };
        if (entryPointIntent)
        {
            probeLines.Add("controller action route service endpoint handled api");
            probeLines.Add("entry point search");
        }

        if (architectureIntent)
        {
            probeLines.Add("architecture overview project structure indexing retrieval adapters");
            probeLines.Add("repository design responsibilities pipeline");
        }

        return string.Join('\n', probeLines);
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

    private static readonly HashSet<string> LowSignalQueryTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "a",
        "an",
        "architecture",
        "current",
        "endpoint",
        "entry",
        "for",
        "handled",
        "handling",
        "how",
        "implemented",
        "in",
        "is",
        "of",
        "on",
        "route",
        "the",
        "to",
        "what",
        "where",
    };

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
