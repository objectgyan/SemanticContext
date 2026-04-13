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
        var errors = SemanticContextRequestValidator.Validate(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var result = await service.IndexAsync(request, cancellationToken).ConfigureAwait(false);
        return Results.Ok(result);
    }

    public static async Task<IResult> QueryAsync(CodeContextQuery query, ICodeContextApplicationService service, CancellationToken cancellationToken)
    {
        var errors = SemanticContextRequestValidator.Validate(query);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var result = await service.QueryAsync(query, cancellationToken).ConfigureAwait(false);
        return Results.Ok(result);
    }
}
