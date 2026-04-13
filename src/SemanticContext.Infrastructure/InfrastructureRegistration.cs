using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SemanticContext.Contracts;

namespace SemanticContext.Infrastructure;

public static class InfrastructureRegistration
{
    public static IServiceCollection AddSemanticContextInfrastructure(this IServiceCollection services)
    {
        services.AddHttpClient("qdrant", (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<QdrantOptions>>().Value;
            client.BaseAddress = new Uri(options.Url, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(30);
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("ApiKey", options.ApiKey);
            }
        });
        services.AddSingleton<IEmbeddingProvider, DeterministicHashEmbeddingProvider>();
        services.AddSingleton<IContentHasher, Sha256ContentHasher>();
        services.AddSingleton<IVectorStore, QdrantVectorStore>();
        return services;
    }
}
