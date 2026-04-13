using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations;

namespace SemanticContext.Contracts;

public enum ReindexMode
{
    Full = 0,
    ChangedOnly = 1,
}

public enum IndexStatus
{
    Completed = 0,
    CompletedWithErrors = 1,
    Failed = 2,
    Skipped = 3,
}

public enum CodeSymbolKind
{
    NamedType = 0,
    Class = 1,
    Interface = 2,
    Record = 3,
    Method = 4,
    Constructor = 5,
    Property = 6,
    ControllerAction = 7,
}

public enum SymbolVisibility
{
    Unknown = 0,
    Public = 1,
    Internal = 2,
    Protected = 3,
    Private = 4,
    ProtectedInternal = 5,
    PrivateProtected = 6,
}

public sealed record IndexRequest
{
    public string SolutionPath { get; init; } = string.Empty;

    public string RepoName { get; init; } = string.Empty;

    public string CommitSha { get; init; } = string.Empty;

    public ReindexMode ReindexMode { get; init; } = ReindexMode.ChangedOnly;
}

public sealed record IndexResult
{
    public IndexStatus Status { get; init; }

    public int FilesScanned { get; init; }

    public int FilesIndexed { get; init; }

    public int ChunksCreated { get; init; }

    public int ChunksUpdated { get; init; }

    public long DurationMs { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

public sealed record CodeContextFilters
{
    public IReadOnlyList<string>? ProjectNames { get; init; }

    public IReadOnlyList<string>? FilePaths { get; init; }

    public IReadOnlyList<CodeSymbolKind>? SymbolKinds { get; init; }

    public IReadOnlyList<string>? Attributes { get; init; }
}

public sealed record CodeContextQuery
{
    public string Query { get; init; } = string.Empty;

    public string RepoName { get; init; } = string.Empty;

    public int TopK { get; init; } = 8;

    public CodeContextFilters? Filters { get; init; }
}

public sealed record CodeContextResult
{
    public double Score { get; init; }

    public string FilePath { get; init; } = string.Empty;

    public string SymbolName { get; init; } = string.Empty;

    public CodeSymbolKind SymbolKind { get; init; }

    public string Signature { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string Snippet { get; init; } = string.Empty;

    public int StartLine { get; init; }

    public int EndLine { get; init; }

    public IReadOnlyList<string> RelatedSymbols { get; init; } = Array.Empty<string>();

    public string? RouteTemplate { get; init; }

    public string? HttpVerb { get; init; }

    public string? ControllerName { get; init; }

    public bool IsApiController { get; init; }
}

public sealed record CodeContextResponse
{
    public string Query { get; init; } = string.Empty;

    public IReadOnlyList<CodeContextResult> Results { get; init; } = Array.Empty<CodeContextResult>();
}

public sealed record CodeChunk
{
    public string Id { get; init; } = string.Empty;

    public string RepoName { get; init; } = string.Empty;

    public string CommitSha { get; init; } = string.Empty;

    public string SolutionPath { get; init; } = string.Empty;

    public string ProjectName { get; init; } = string.Empty;

    public string FilePath { get; init; } = string.Empty;

    public string SymbolId { get; init; } = string.Empty;

    public CodeSymbolKind SymbolKind { get; init; }

    public string SymbolName { get; init; } = string.Empty;

    public string Namespace { get; init; } = string.Empty;

    public string ContainingType { get; init; } = string.Empty;

    public string Signature { get; init; } = string.Empty;

    public IReadOnlyList<string> Attributes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();

    public SymbolVisibility Visibility { get; init; } = SymbolVisibility.Unknown;

    public int StartLine { get; init; }

    public int EndLine { get; init; }

    public string ChunkText { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string ContentHash { get; init; } = string.Empty;

    public string? SourceSnippet { get; init; }

    public string? RouteTemplate { get; init; }

    public string? HttpVerb { get; init; }

    public string? ControllerName { get; init; }

    public bool IsApiController { get; init; }

    public string? XmlDocumentation { get; init; }
}

public sealed record VectorRecord
{
    public string Id { get; init; } = string.Empty;

    public IReadOnlyList<float> Vector { get; init; } = Array.Empty<float>();

    public IReadOnlyDictionary<string, object?> Payload { get; init; } = new Dictionary<string, object?>();
}

public sealed record VectorSearchRequest
{
    public IReadOnlyList<float> QueryVector { get; init; } = Array.Empty<float>();

    public int TopK { get; init; } = 8;

    public string? RepoName { get; init; }

    public CodeContextFilters? Filters { get; init; }
}

public sealed record VectorSearchResult
{
    public string Id { get; init; } = string.Empty;

    public double Score { get; init; }

    public IReadOnlyDictionary<string, object?> Payload { get; init; } = new Dictionary<string, object?>();
}

public interface ICodeIndexer
{
    Task<IndexResult> IndexAsync(IndexRequest request, CancellationToken cancellationToken = default);
}

public interface ICodeContextRetriever
{
    Task<CodeContextResponse> QueryAsync(CodeContextQuery query, CancellationToken cancellationToken = default);
}

public interface IEmbeddingProvider
{
    Task<IReadOnlyList<float>> CreateEmbeddingAsync(string input, CancellationToken cancellationToken = default);
}

public interface IVectorStore
{
    Task UpsertAsync(IReadOnlyCollection<VectorRecord> records, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(VectorSearchRequest request, CancellationToken cancellationToken = default);

    Task DeleteByRepoAsync(string repoName, CancellationToken cancellationToken = default);

    Task DeleteByIdsAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default);
}

public interface ICodeSummaryGenerator
{
    string GenerateSummary(CodeChunk chunk);
}

public interface ICodeContextApplicationService
{
    Task<IndexResult> IndexAsync(IndexRequest request, CancellationToken cancellationToken = default);

    Task<CodeContextResponse> QueryAsync(CodeContextQuery query, CancellationToken cancellationToken = default);
}

public interface IContentHasher
{
    string ComputeHash(string input);
}

public sealed record QdrantOptions
{
    [Required]
    [Url]
    public string Url { get; init; } = "http://localhost:6333";

    [Required]
    public string CollectionName { get; init; } = "semanticcontext";

    public string? ApiKey { get; init; }

    [Range(1, 4096)]
    public int VectorSize { get; init; } = 256;
}

public sealed record EmbeddingProviderOptions
{
    [Range(1, 4096)]
    public int Dimension { get; init; } = 256;
}

public sealed record IndexingOptions
{
    [Range(1, 1000)]
    public int SnippetLength { get; init; } = 220;

    [Required]
    public string CacheDirectory { get; init; } = ".semanticcontext";

    [Range(1, 10000)]
    public int SearchWindowSize { get; init; } = 50;
}

public sealed record RetrievalOptions
{
    [Range(1, 100)]
    public int RerankWindowSize { get; init; } = 25;

    [Range(0, 100)]
    public int KeywordBoostMax { get; init; } = 12;
}
