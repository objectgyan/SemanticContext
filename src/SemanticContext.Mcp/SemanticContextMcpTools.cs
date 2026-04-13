using System.ComponentModel;
using ModelContextProtocol.Server;
using SemanticContext.Contracts;

namespace SemanticContext.Mcp;

[McpServerToolType]
public static class SemanticContextMcpTools
{
    [McpServerTool]
    [Description("Search indexed code semantically across a repository.")]
    public static Task<CodeContextResponse> SemanticSearchAsync(
        CodeContextQuery query,
        ISemanticContextMcpFacade facade,
        CancellationToken cancellationToken = default)
    {
        return facade.SemanticSearchAsync(query, cancellationToken);
    }

    [McpServerTool]
    [Description("Index a solution into the semantic context store.")]
    public static Task<IndexResult> IndexSolutionAsync(
        IndexRequest request,
        ISemanticContextMcpFacade facade,
        CancellationToken cancellationToken = default)
    {
        return facade.IndexSolutionAsync(request, cancellationToken);
    }

    [McpServerTool]
    [Description("Get structured context for a symbol within a file.")]
    public static Task<SymbolContextResponse> GetSymbolContextAsync(
        string repoName,
        string filePath,
        string symbolName,
        ISemanticContextMcpFacade facade,
        CancellationToken cancellationToken = default)
    {
        return facade.GetSymbolContextAsync(repoName, filePath, symbolName, cancellationToken);
    }
}
