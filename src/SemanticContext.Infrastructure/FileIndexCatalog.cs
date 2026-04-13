using System.Text.Json;
using Microsoft.Extensions.Options;
using SemanticContext.Contracts;

namespace SemanticContext.Infrastructure;

public sealed class FileIndexCatalog : IIndexCatalog
{
    private readonly IndexingOptions _options;

    public FileIndexCatalog(IOptions<IndexingOptions> options)
    {
        _options = options.Value;
    }

    public async Task<RepositoryMetadata?> GetRepositoryMetadataAsync(string repoName, CancellationToken cancellationToken = default)
    {
        var manifest = await LoadManifestAsync(repoName, cancellationToken).ConfigureAwait(false);
        if (manifest is null || manifest.Documents.Count == 0)
        {
            return null;
        }

        var documents = manifest.Documents.Values.ToArray();
        return new RepositoryMetadata
        {
            RepoName = repoName,
            DocumentCount = documents.Length,
            ChunkCount = documents.Sum(GetChunkCount),
            ProjectCount = documents
                .Select(document => document.ProjectName)
                .Where(projectName => !string.IsNullOrWhiteSpace(projectName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            ProjectNames = documents
                .Select(document => document.ProjectName)
                .Where(projectName => !string.IsNullOrWhiteSpace(projectName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(projectName => projectName, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            FilePaths = documents
                .Select(document => document.FilePath)
                .Where(filePath => !string.IsNullOrWhiteSpace(filePath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(filePath => filePath, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            SymbolKinds = documents
                .SelectMany(document => document.SymbolKinds)
                .Where(kind => !string.IsNullOrWhiteSpace(kind))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            LastIndexedUtc = documents
                .Select(document => document.LastIndexedUtc)
                .Where(dateTimeOffset => dateTimeOffset > DateTimeOffset.MinValue)
                .OrderByDescending(dateTimeOffset => dateTimeOffset)
                .FirstOrDefault(),
        };
    }

    public async Task<IReadOnlyList<ProjectMetadata>> GetProjectMetadataAsync(string repoName, CancellationToken cancellationToken = default)
    {
        var manifest = await LoadManifestAsync(repoName, cancellationToken).ConfigureAwait(false);
        if (manifest is null || manifest.Documents.Count == 0)
        {
            return Array.Empty<ProjectMetadata>();
        }

        return manifest.Documents.Values
            .GroupBy(document => string.IsNullOrWhiteSpace(document.ProjectName) ? "Unknown" : document.ProjectName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProjectMetadata
            {
                RepoName = repoName,
                ProjectName = group.Key,
                DocumentCount = group.Count(),
                ChunkCount = group.Sum(GetChunkCount),
                FilePaths = group
                    .Select(document => document.FilePath)
                    .Where(filePath => !string.IsNullOrWhiteSpace(filePath))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(filePath => filePath, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                SymbolKinds = group
                    .SelectMany(document => document.SymbolKinds)
                    .Where(kind => !string.IsNullOrWhiteSpace(kind))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
            })
            .OrderBy(project => project.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IndexManifest?> LoadManifestAsync(string repoName, CancellationToken cancellationToken)
    {
        var path = GetManifestPath(repoName);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<IndexManifest>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private string GetManifestPath(string repoName)
    {
        var safeRepo = new string(repoName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch is '/' or '\\' ? '_' : ch).ToArray());
        return Path.Combine(_options.CacheDirectory, safeRepo, "manifest.json");
    }

    private static int GetChunkCount(DocumentManifest document)
    {
        return document.ChunkCount > 0 ? document.ChunkCount : document.ChunkIds.Count;
    }
}
