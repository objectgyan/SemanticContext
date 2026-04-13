using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SemanticContext.Contracts;

namespace SemanticContext.Tests;

public sealed class ApiIntegrationTests
{
    [Fact]
    public async Task Index_endpoint_returns_validation_problem_details_for_invalid_input()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/index", new
        {
            solutionPath = "/repos/sample/sample.txt",
            repoName = "",
            commitSha = "",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(payload.RootElement.TryGetProperty("type", out _));
        var errors = payload.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty(nameof(IndexRequest.SolutionPath), out _));
        Assert.True(errors.TryGetProperty(nameof(IndexRequest.RepoName), out _));
        Assert.True(errors.TryGetProperty(nameof(IndexRequest.CommitSha), out _));
    }

    [Fact]
    public async Task Query_endpoint_delegates_through_the_http_pipeline()
    {
        var service = new RecordingApplicationService
        {
            QueryResultToReturn = new CodeContextResponse
            {
                Query = "order validation",
                Results =
                [
                    new CodeContextResult
                    {
                        SymbolName = "ValidateOrderAsync",
                        FilePath = "src/Orders/OrderService.cs",
                    },
                ],
            },
        };

        await using var factory = CreateFactory(service);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/query", new
        {
            query = "order validation",
            repoName = "sample",
            topK = 5,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("order validation", payload.RootElement.GetProperty("query").GetString());
        var results = payload.RootElement.GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal("ValidateOrderAsync", results[0].GetProperty("symbolName").GetString());
        Assert.Equal(1, service.QueryCallCount);
    }

    private static WebApplicationFactory<Program> CreateFactory(RecordingApplicationService? service = null)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("IntegrationTest");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<ICodeContextApplicationService>();
                    services.AddSingleton<ICodeContextApplicationService>(service ?? new RecordingApplicationService());
                });
            });
    }
}
