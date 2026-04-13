using Microsoft.AspNetCore.Mvc;
using SemanticContext.Contracts;

namespace SemanticContext.Api;

public static class SemanticContextEndpointHandlers
{
    public static IResult Health()
    {
        return Results.Ok(new { status = "Healthy" });
    }

    public static async Task<IResult> IndexAsync(IndexRequest request, ICodeContextApplicationService service, CancellationToken cancellationToken)
    {
        var errors = ValidateIndexRequest(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var result = await service.IndexAsync(request, cancellationToken).ConfigureAwait(false);
        return Results.Ok(result);
    }

    public static async Task<IResult> QueryAsync(CodeContextQuery query, ICodeContextApplicationService service, CancellationToken cancellationToken)
    {
        var errors = ValidateQueryRequest(query);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var result = await service.QueryAsync(query, cancellationToken).ConfigureAwait(false);
        return Results.Ok(result);
    }

    private static Dictionary<string, string[]> ValidateIndexRequest(IndexRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        AddIfWhiteSpace(errors, nameof(IndexRequest.SolutionPath), request.SolutionPath);
        AddIfWhiteSpace(errors, nameof(IndexRequest.RepoName), request.RepoName);
        AddIfWhiteSpace(errors, nameof(IndexRequest.CommitSha), request.CommitSha);
        return errors;
    }

    private static Dictionary<string, string[]> ValidateQueryRequest(CodeContextQuery query)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        AddIfWhiteSpace(errors, nameof(CodeContextQuery.Query), query.Query);
        AddIfWhiteSpace(errors, nameof(CodeContextQuery.RepoName), query.RepoName);
        if (query.TopK <= 0)
        {
            errors[nameof(CodeContextQuery.TopK)] = ["TopK must be greater than zero."];
        }

        return errors;
    }

    private static void AddIfWhiteSpace(Dictionary<string, string[]> errors, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[key] = [$"{key} is required."];
        }
    }
}

