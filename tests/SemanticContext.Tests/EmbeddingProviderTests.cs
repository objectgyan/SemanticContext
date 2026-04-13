using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SemanticContext.Contracts;
using SemanticContext.Infrastructure;

namespace SemanticContext.Tests;

public sealed class EmbeddingProviderTests
{
    [Fact]
    public async Task Remote_http_embedding_provider_parses_embedding_payload()
    {
        var handler = new RecordingHttpMessageHandler(
            HttpStatusCode.OK,
            """
            {"embedding":[0.25,0.5,0.75]}
            """);

        var services = new ServiceCollection();
        services.AddHttpClient("embedding")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .ConfigureHttpClient(client => client.BaseAddress = new Uri("https://example.com"));
        services.AddSingleton(Options.Create(new EmbeddingProviderOptions
        {
            Kind = EmbeddingProviderKind.RemoteHttp,
            EndpointUrl = "https://example.com",
            Model = "text-embedding",
            TimeoutSeconds = 30,
        }));
        services.AddSingleton<ILogger<RemoteHttpEmbeddingProvider>>(NullLogger<RemoteHttpEmbeddingProvider>.Instance);
        services.AddSingleton<IEmbeddingProvider, RemoteHttpEmbeddingProvider>();

        await using var provider = services.BuildServiceProvider();
        var embeddingProvider = provider.GetRequiredService<IEmbeddingProvider>();

        var embedding = await embeddingProvider.CreateEmbeddingAsync("hello world");

        Assert.Equal(new[] { 0.25f, 0.5f, 0.75f }, embedding);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("/embeddings", handler.LastRequestUri?.AbsolutePath);
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseBody;

        public HttpMethod? LastMethod { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        public RecordingHttpMessageHandler(HttpStatusCode statusCode, string responseBody)
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastRequestUri = request.RequestUri;
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
