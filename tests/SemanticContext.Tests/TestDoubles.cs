using SemanticContext.Contracts;
using SemanticContext.Mcp;
using System.Globalization;
using System.Text;

namespace SemanticContext.Tests;

internal sealed class InMemoryVectorStore : IVectorStore
{
    private readonly Dictionary<string, VectorRecord> _records = new(StringComparer.Ordinal);

    public IReadOnlyCollection<VectorRecord> Records => _records.Values.ToArray();

    public Task UpsertAsync(IReadOnlyCollection<VectorRecord> records, CancellationToken cancellationToken = default)
    {
        foreach (var record in records)
        {
            _records[record.Id] = record;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(VectorSearchRequest request, CancellationToken cancellationToken = default)
    {
        var results = _records.Values
            .Where(record => MatchesFilters(record.Payload, request.RepoName, request.Filters))
            .Select(record => new VectorSearchResult
            {
                Id = record.Id,
                Score = CosineSimilarity(record.Vector, request.QueryVector),
                Payload = record.Payload,
            })
            .OrderByDescending(result => result.Score)
            .Take(request.TopK)
            .ToList();

        return Task.FromResult<IReadOnlyList<VectorSearchResult>>(results);
    }

    public Task DeleteByRepoAsync(string repoName, CancellationToken cancellationToken = default)
    {
        var keys = _records.Values
            .Where(record => string.Equals(GetString(record.Payload, "repoName"), repoName, StringComparison.OrdinalIgnoreCase))
            .Select(record => record.Id)
            .ToArray();

        foreach (var key in keys)
        {
            _records.Remove(key);
        }

        return Task.CompletedTask;
    }

    public Task DeleteByIdsAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        foreach (var id in ids)
        {
            _records.Remove(id);
        }

        return Task.CompletedTask;
    }

    private static bool MatchesFilters(IReadOnlyDictionary<string, object?> payload, string? repoName, CodeContextFilters? filters)
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

        if (filters.ProjectNames is { Count: > 0 } projectNames &&
            !projectNames.Any(name => string.Equals(name, GetString(payload, "projectName"), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (filters.FilePaths is { Count: > 0 } filePaths &&
            !filePaths.Any(path => string.Equals(path, GetString(payload, "filePath"), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (filters.SymbolKinds is { Count: > 0 } symbolKinds)
        {
            var actual = ParseSymbolKind(GetString(payload, "symbolKind"));
            if (!symbolKinds.Any(filter => filter == CodeSymbolKind.NamedType
                    ? actual is CodeSymbolKind.Class or CodeSymbolKind.Interface or CodeSymbolKind.Record
                    : actual == filter))
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

    private static double CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        var length = Math.Min(left.Count, right.Count);
        if (length == 0)
        {
            return 0;
        }

        var dot = 0.0;
        var leftMagnitude = 0.0;
        var rightMagnitude = 0.0;
        for (var i = 0; i < length; i++)
        {
            dot += left[i] * right[i];
            leftMagnitude += left[i] * left[i];
            rightMagnitude += right[i] * right[i];
        }

        if (leftMagnitude == 0 || rightMagnitude == 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
    }

    private static string GetString(IReadOnlyDictionary<string, object?> payload, string key)
    {
        return payload.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
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

        return value.ToString() is string text
            ? text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();
    }

    private static CodeSymbolKind ParseSymbolKind(string? value)
    {
        return Enum.TryParse<CodeSymbolKind>(value, true, out var parsed) ? parsed : CodeSymbolKind.NamedType;
    }
}

internal sealed class RecordingApplicationService : ICodeContextApplicationService
{
    public IndexRequest? LastIndexRequest { get; private set; }

    public CodeContextQuery? LastQuery { get; private set; }

    public IndexResult IndexResultToReturn { get; set; } = new()
    {
        Status = IndexStatus.Completed,
    };

    public CodeContextResponse QueryResultToReturn { get; set; } = new();

    public int IndexCallCount { get; private set; }

    public int QueryCallCount { get; private set; }

    public Task<IndexResult> IndexAsync(IndexRequest request, CancellationToken cancellationToken = default)
    {
        IndexCallCount++;
        LastIndexRequest = request;
        return Task.FromResult(IndexResultToReturn);
    }

    public Task<CodeContextResponse> QueryAsync(CodeContextQuery query, CancellationToken cancellationToken = default)
    {
        QueryCallCount++;
        LastQuery = query;
        return Task.FromResult(QueryResultToReturn);
    }
}

internal sealed class RecordingIndexCatalog : IIndexCatalog
{
    public RepositoryMetadata? RepositoryMetadataToReturn { get; set; }

    public IReadOnlyList<ProjectMetadata> ProjectMetadataToReturn { get; set; } = [];

    public int RepositoryMetadataCallCount { get; private set; }

    public int ProjectMetadataCallCount { get; private set; }

    public string? LastRepoName { get; private set; }

    public Task<RepositoryMetadata?> GetRepositoryMetadataAsync(string repoName, CancellationToken cancellationToken = default)
    {
        RepositoryMetadataCallCount++;
        LastRepoName = repoName;
        return Task.FromResult(RepositoryMetadataToReturn);
    }

    public Task<IReadOnlyList<ProjectMetadata>> GetProjectMetadataAsync(string repoName, CancellationToken cancellationToken = default)
    {
        ProjectMetadataCallCount++;
        LastRepoName = repoName;
        return Task.FromResult(ProjectMetadataToReturn);
    }
}

internal static class FixturePaths
{
    public static string TinySolutionRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TinySolution"));

    public static string TinySolutionPath => Path.Combine(TinySolutionRoot, "TinySolution.sln");
}
