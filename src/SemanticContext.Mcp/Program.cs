using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using SemanticContext.Application;
using SemanticContext.Contracts;
using SemanticContext.Infrastructure;
using SemanticContext.Indexer;
using SemanticContext.Retrieval;
using SemanticContext.Mcp;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddOptions<QdrantOptions>()
    .BindConfiguration("Qdrant")
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<EmbeddingProviderOptions>()
    .BindConfiguration("EmbeddingProvider")
    .ValidateDataAnnotations()
    .Validate(options => options.Kind != EmbeddingProviderKind.RemoteHttp || !string.IsNullOrWhiteSpace(options.EndpointUrl), "EmbeddingProvider:EndpointUrl is required when Kind is RemoteHttp.")
    .Validate(options => options.Kind != EmbeddingProviderKind.RemoteHttp || Uri.IsWellFormedUriString(options.EndpointUrl, UriKind.Absolute), "EmbeddingProvider:EndpointUrl must be an absolute URI when Kind is RemoteHttp.")
    .ValidateOnStart();

builder.Services.AddOptions<IndexingOptions>()
    .BindConfiguration("Indexing")
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<RetrievalOptions>()
    .BindConfiguration("Retrieval")
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSemanticContextInfrastructure();
builder.Services.AddSemanticContextIndexer();
builder.Services.AddSemanticContextRetrieval();
builder.Services.AddSemanticContextApplication();
builder.Services.AddSemanticContextMcp();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly();

var app = builder.Build();

await app.RunAsync().ConfigureAwait(false);
