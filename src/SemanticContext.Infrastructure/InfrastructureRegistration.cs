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
        services.AddHttpClient("embedding", (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<EmbeddingProviderOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(options.EndpointUrl))
            {
                client.BaseAddress = new Uri(options.EndpointUrl, UriKind.Absolute);
            }

            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 300));
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
            }
        });
        services.AddSingleton<IEmbeddingProvider>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<EmbeddingProviderOptions>>().Value;
            return options.Kind == EmbeddingProviderKind.RemoteHttp
                ? ActivatorUtilities.CreateInstance<RemoteHttpEmbeddingProvider>(sp)
                : ActivatorUtilities.CreateInstance<DeterministicHashEmbeddingProvider>(sp);
        });
        services.AddSingleton<IContentHasher, Sha256ContentHasher>();
        services.AddSingleton<IVectorStore, QdrantVectorStore>();
        services.AddSingleton<IIndexCatalog, FileIndexCatalog>();
        return services;
    }
}
