using System.Text;
using System.Text.Json;
using SemanticContext.Contracts;

namespace SemanticContext.Indexer;

internal static class ChunkTextBuilder
{
    public static string Build(CodeChunk chunk)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Symbol: {chunk.SymbolName}");
        builder.AppendLine($"Kind: {chunk.SymbolKind}");
        builder.AppendLine($"Namespace: {chunk.Namespace}");
        builder.AppendLine($"ContainingType: {chunk.ContainingType}");
        builder.AppendLine($"Project: {chunk.ProjectName}");
        builder.AppendLine($"File: {chunk.FilePath}");
        builder.AppendLine($"Signature: {chunk.Signature}");
        builder.AppendLine($"Attributes: {string.Join(", ", chunk.Attributes)}");
        builder.AppendLine($"Dependencies: {string.Join(", ", chunk.Dependencies)}");
        builder.AppendLine($"ControllerName: {chunk.ControllerName}");
        builder.AppendLine($"IsApiController: {chunk.IsApiController}");
        builder.AppendLine();
        builder.AppendLine("Summary:");
        builder.AppendLine(chunk.Summary);
        builder.AppendLine();
        builder.AppendLine("Code:");
        builder.AppendLine(chunk.SourceSnippet ?? string.Empty);
        return builder.ToString().TrimEnd();
    }
}

internal sealed class IndexManifestStore
{
    private readonly IndexingOptions _options;

    public IndexManifestStore(IndexingOptions options)
    {
        _options = options;
    }

    public async Task<IndexManifest> LoadAsync(string repoName, CancellationToken cancellationToken)
    {
        var path = GetManifestPath(repoName);
        if (!File.Exists(path))
        {
            return new IndexManifest();
        }

        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<IndexManifest>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return manifest ?? new IndexManifest();
    }

    public async Task SaveAsync(string repoName, IndexManifest manifest, CancellationToken cancellationToken)
    {
        var path = GetManifestPath(repoName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, manifest, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private string GetManifestPath(string repoName)
    {
        var safeRepo = new string(repoName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch is '/' or '\\' ? '_' : ch).ToArray());
        return Path.Combine(_options.CacheDirectory, safeRepo, "manifest.json");
    }
}
