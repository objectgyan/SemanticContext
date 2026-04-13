using Microsoft.Extensions.DependencyInjection;
using SemanticContext.Contracts;

namespace SemanticContext.Mcp;

public sealed record SymbolContextResponse
{
    public string RepoName { get; init; } = string.Empty;

    public string FilePath { get; init; } = string.Empty;

    public string SymbolName { get; init; } = string.Empty;

    public string ProjectName { get; init; } = string.Empty;

    public CodeContextResult? Result { get; init; }
}

public interface ISemanticContextMcpFacade
{
    Task<CodeContextResponse> SemanticSearchAsync(CodeContextQuery query, CancellationToken cancellationToken = default);

    Task<IndexResult> IndexSolutionAsync(IndexRequest request, CancellationToken cancellationToken = default);

    Task<SymbolContextResponse> GetSymbolContextAsync(string repoName, string filePath, string symbolName, CancellationToken cancellationToken = default);

    Task<RepositoryMetadata?> GetRepositoryMetadataAsync(string repoName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectMetadata>> GetProjectMetadataAsync(string repoName, CancellationToken cancellationToken = default);
}

public sealed class SemanticContextMcpFacade : ISemanticContextMcpFacade
{
    private readonly ICodeContextApplicationService _applicationService;
    private readonly IIndexCatalog _indexCatalog;

    public SemanticContextMcpFacade(ICodeContextApplicationService applicationService, IIndexCatalog indexCatalog)
    {
        _applicationService = applicationService;
        _indexCatalog = indexCatalog;
    }

    public Task<CodeContextResponse> SemanticSearchAsync(CodeContextQuery query, CancellationToken cancellationToken = default)
    {
        return _applicationService.QueryAsync(query, cancellationToken);
    }

    public Task<IndexResult> IndexSolutionAsync(IndexRequest request, CancellationToken cancellationToken = default)
    {
        return _applicationService.IndexAsync(request, cancellationToken);
    }

    public async Task<SymbolContextResponse> GetSymbolContextAsync(string repoName, string filePath, string symbolName, CancellationToken cancellationToken = default)
    {
        var response = await _applicationService.QueryAsync(new CodeContextQuery
        {
            Query = symbolName,
            RepoName = repoName,
            TopK = 10,
            Filters = new CodeContextFilters
            {
                FilePaths = [filePath],
            },
        }, cancellationToken).ConfigureAwait(false);

        var exact = response.Results.FirstOrDefault(result =>
            string.Equals(result.SymbolName, symbolName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(result.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

        return new SymbolContextResponse
        {
            RepoName = repoName,
            FilePath = filePath,
            SymbolName = symbolName,
            ProjectName = exact?.ProjectName ?? response.Results.FirstOrDefault()?.ProjectName ?? string.Empty,
            Result = exact ?? response.Results.FirstOrDefault(),
        };
    }

    public Task<RepositoryMetadata?> GetRepositoryMetadataAsync(string repoName, CancellationToken cancellationToken = default)
    {
        return _indexCatalog.GetRepositoryMetadataAsync(repoName, cancellationToken);
    }

    public Task<IReadOnlyList<ProjectMetadata>> GetProjectMetadataAsync(string repoName, CancellationToken cancellationToken = default)
    {
        return _indexCatalog.GetProjectMetadataAsync(repoName, cancellationToken);
    }
}

public static class McpRegistration
{
    public static IServiceCollection AddSemanticContextMcp(this IServiceCollection services)
    {
        services.AddScoped<ISemanticContextMcpFacade, SemanticContextMcpFacade>();
        return services;
    }
}
