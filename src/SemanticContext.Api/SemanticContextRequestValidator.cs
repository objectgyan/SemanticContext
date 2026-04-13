using SemanticContext.Contracts;

namespace SemanticContext.Api;

public static class SemanticContextRequestValidator
{
    public static Dictionary<string, string[]> Validate(IndexRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        AddRequired(errors, nameof(IndexRequest.SolutionPath), request.SolutionPath);
        AddRequired(errors, nameof(IndexRequest.RepoName), request.RepoName);
        AddRequired(errors, nameof(IndexRequest.CommitSha), request.CommitSha);

        if (!string.IsNullOrWhiteSpace(request.SolutionPath) &&
            !request.SolutionPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            errors[nameof(IndexRequest.SolutionPath)] = ["SolutionPath must point to a .sln file."];
        }

        return errors;
    }

    public static Dictionary<string, string[]> Validate(CodeContextQuery query)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        AddRequired(errors, nameof(CodeContextQuery.Query), query.Query);
        AddRequired(errors, nameof(CodeContextQuery.RepoName), query.RepoName);

        if (query.TopK <= 0)
        {
            errors[nameof(CodeContextQuery.TopK)] = ["TopK must be greater than zero."];
        }
        else if (query.TopK > 100)
        {
            errors[nameof(CodeContextQuery.TopK)] = ["TopK must be 100 or less."];
        }

        ValidateFilters(errors, query.Filters);
        return errors;
    }

    private static void ValidateFilters(Dictionary<string, string[]> errors, CodeContextFilters? filters)
    {
        if (filters is null)
        {
            return;
        }

        ValidateList(errors, "filters.projectNames", filters.ProjectNames, "ProjectNames");
        ValidateList(errors, "filters.filePaths", filters.FilePaths, "FilePaths");
        ValidateList(errors, "filters.attributes", filters.Attributes, "Attributes");

        if (filters.SymbolKinds is { Count: > 0 } symbolKinds && symbolKinds.Any(kind => !Enum.IsDefined(typeof(CodeSymbolKind), kind)))
        {
            errors["filters.symbolKinds"] = ["SymbolKinds contains an invalid value."];
        }
    }

    private static void ValidateList(Dictionary<string, string[]> errors, string key, IReadOnlyList<string>? values, string label)
    {
        if (values is null || values.Count == 0)
        {
            return;
        }

        var invalidIndexes = values
            .Select((value, index) => new { value, index })
            .Where(item => string.IsNullOrWhiteSpace(item.value))
            .Select(item => item.index)
            .ToArray();

        if (invalidIndexes.Length > 0)
        {
            errors[key] = [$"{label} contains empty values at indexes: {string.Join(", ", invalidIndexes)}."];
        }
    }

    private static void AddRequired(Dictionary<string, string[]> errors, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[key] = [$"{key} is required."];
        }
    }
}
