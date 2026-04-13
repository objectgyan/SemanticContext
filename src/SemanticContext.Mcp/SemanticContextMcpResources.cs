using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using SemanticContext.Contracts;

namespace SemanticContext.Mcp;

[McpServerResourceType]
public static class SemanticContextMcpResources
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    static SemanticContextMcpResources()
    {
        SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    }

    [McpServerResource(UriTemplate = "semanticcontext://repositories{?repoName}", Name = "repository-metadata", MimeType = "application/json")]
    [Description("Returns repository metadata for a semantic context indexed repository.")]
    public static async Task<string> GetRepositoryMetadataAsync(
        string repoName,
        ISemanticContextMcpFacade facade,
        CancellationToken cancellationToken = default)
    {
        var metadata = await facade.GetRepositoryMetadataAsync(repoName, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(metadata, SerializerOptions);
    }

    [McpServerResource(UriTemplate = "semanticcontext://projects{?repoName,projectName}", Name = "project-metadata", MimeType = "application/json")]
    [Description("Returns project summaries for a semantic context indexed repository.")]
    public static async Task<string> GetProjectMetadataAsync(
        string repoName,
        string? projectName,
        ISemanticContextMcpFacade facade,
        CancellationToken cancellationToken = default)
    {
        var projects = await facade.GetProjectMetadataAsync(repoName, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(projectName))
        {
            projects = projects
                .Where(project => string.Equals(project.ProjectName, projectName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        return JsonSerializer.Serialize(projects, SerializerOptions);
    }

    [McpServerResource(UriTemplate = "semanticcontext://symbols{?repoName,filePath,symbolName}", Name = "symbol-context", MimeType = "application/json")]
    [Description("Returns symbol-level code context for a repository file and symbol name.")]
    public static async Task<string> GetSymbolContextAsync(
        string repoName,
        string filePath,
        string symbolName,
        ISemanticContextMcpFacade facade,
        CancellationToken cancellationToken = default)
    {
        var symbolContext = await facade.GetSymbolContextAsync(repoName, filePath, symbolName, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(symbolContext, SerializerOptions);
    }
}
