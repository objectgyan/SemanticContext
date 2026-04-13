using SemanticContext.Api;
using SemanticContext.Contracts;
using SemanticContext.Mcp;

namespace SemanticContext.Tests;

public sealed class AdapterBoundaryTests
{
    [Fact]
    public async Task Http_index_handler_delegates_to_application_service()
    {
        var service = new RecordingApplicationService();
        var request = new IndexRequest
        {
            SolutionPath = "/repos/sample/sample.sln",
            RepoName = "sample",
            CommitSha = "abc123",
            ReindexMode = ReindexMode.ChangedOnly,
        };

        var result = await SemanticContextEndpointHandlers.IndexAsync(request, service, CancellationToken.None);

        Assert.Equal(1, service.IndexCallCount);
        Assert.Equal(request, service.LastIndexRequest);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Http_query_handler_delegates_to_application_service()
    {
        var service = new RecordingApplicationService
        {
            QueryResultToReturn = new CodeContextResponse
            {
                Query = "order validation",
            },
        };

        var query = new CodeContextQuery
        {
            Query = "order validation",
            RepoName = "sample",
            TopK = 5,
        };

        var result = await SemanticContextEndpointHandlers.QueryAsync(query, service, CancellationToken.None);

        Assert.Equal(1, service.QueryCallCount);
        Assert.Equal(query, service.LastQuery);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Mcp_facade_delegates_to_application_service()
    {
        var catalog = new RecordingIndexCatalog
        {
            RepositoryMetadataToReturn = new RepositoryMetadata
            {
                RepoName = "repo",
                DocumentCount = 3,
            },
            ProjectMetadataToReturn =
            [
                new ProjectMetadata
                {
                    RepoName = "repo",
                    ProjectName = "ProjectA",
                    DocumentCount = 1,
                },
            ],
        };

        var service = new RecordingApplicationService
        {
            QueryResultToReturn = new CodeContextResponse
            {
                Query = "search",
                Results = [new CodeContextResult { SymbolName = "GetOrderAsync" }],
            },
            IndexResultToReturn = new IndexResult
            {
                Status = IndexStatus.Completed,
            },
        };

        var facade = new SemanticContextMcpFacade(service, catalog);

        var search = await facade.SemanticSearchAsync(new CodeContextQuery
        {
            Query = "search",
            RepoName = "repo",
        });
        var index = await facade.IndexSolutionAsync(new IndexRequest
        {
            SolutionPath = "/tmp/sample.sln",
            RepoName = "repo",
            CommitSha = "abc",
        });
        var symbol = await facade.GetSymbolContextAsync("repo", "file.cs", "GetOrderAsync");
        var repository = await facade.GetRepositoryMetadataAsync("repo");
        var projects = await facade.GetProjectMetadataAsync("repo");

        Assert.Equal(2, service.QueryCallCount);
        Assert.Equal(1, service.IndexCallCount);
        Assert.Equal("search", search.Query);
        Assert.Equal(IndexStatus.Completed, index.Status);
        Assert.Equal("GetOrderAsync", symbol.SymbolName);
        Assert.Equal("repo", repository?.RepoName);
        Assert.Single(projects);
        Assert.Equal("ProjectA", projects[0].ProjectName);
        Assert.Equal(1, catalog.RepositoryMetadataCallCount);
        Assert.Equal(1, catalog.ProjectMetadataCallCount);
    }
}
