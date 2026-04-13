using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc;
using SemanticContext.Api;
using SemanticContext.Application;
using SemanticContext.Contracts;
using SemanticContext.Infrastructure;
using SemanticContext.Indexer;
using SemanticContext.Retrieval;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

builder.Services.AddOptions<QdrantOptions>()
    .BindConfiguration("Qdrant")
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<EmbeddingProviderOptions>()
    .BindConfiguration("EmbeddingProvider")
    .ValidateDataAnnotations()
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

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerPathFeature>();
        var problem = new ProblemDetails
        {
            Title = "Unhandled server error",
            Status = StatusCodes.Status500InternalServerError,
            Detail = feature?.Error.Message,
        };

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(problem, cancellationToken: context.RequestAborted).ConfigureAwait(false);
    });
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapSemanticContextEndpoints();

app.Run();

public partial class Program;

static class SemanticContextEndpointMapping
{
    public static IEndpointRouteBuilder MapSemanticContextEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", SemanticContextEndpointHandlers.Health)
            .WithName("Health")
            .WithOpenApi();
        app.MapPost("/index", SemanticContextEndpointHandlers.IndexAsync)
            .WithName("IndexSolution")
            .Accepts<IndexRequest>("application/json")
            .Produces<IndexResult>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .WithOpenApi();
        app.MapPost("/query", SemanticContextEndpointHandlers.QueryAsync)
            .WithName("QueryCodeContext")
            .Accepts<CodeContextQuery>("application/json")
            .Produces<CodeContextResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .WithOpenApi();

        return app;
    }
}
