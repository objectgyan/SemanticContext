using Microsoft.Extensions.DependencyInjection;
using SemanticContext.Contracts;

namespace SemanticContext.Application;

public sealed class CodeContextApplicationService : ICodeContextApplicationService
{
    private readonly ICodeIndexer _indexer;
    private readonly ICodeContextRetriever _retriever;

    public CodeContextApplicationService(ICodeIndexer indexer, ICodeContextRetriever retriever)
    {
        _indexer = indexer;
        _retriever = retriever;
    }

    public Task<IndexResult> IndexAsync(IndexRequest request, CancellationToken cancellationToken = default)
    {
        return _indexer.IndexAsync(request, cancellationToken);
    }

    public Task<CodeContextResponse> QueryAsync(CodeContextQuery query, CancellationToken cancellationToken = default)
    {
        return _retriever.QueryAsync(query, cancellationToken);
    }
}

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddSemanticContextApplication(this IServiceCollection services)
    {
        services.AddScoped<ICodeContextApplicationService, CodeContextApplicationService>();
        return services;
    }
}

