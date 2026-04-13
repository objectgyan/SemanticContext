using Microsoft.Extensions.DependencyInjection;
using SemanticContext.Contracts;

namespace SemanticContext.Indexer;

public static class IndexerRegistration
{
    public static IServiceCollection AddSemanticContextIndexer(this IServiceCollection services)
    {
        services.AddSingleton<ICodeSummaryGenerator, DeterministicCodeSummaryGenerator>();
        services.AddScoped<ICodeIndexer, SolutionCodeIndexer>();
        return services;
    }
}

