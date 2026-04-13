using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SemanticContext.Contracts;

namespace SemanticContext.Indexer;

public sealed class SolutionCodeIndexer : ICodeIndexer
{
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IVectorStore _vectorStore;
    private readonly ICodeSummaryGenerator _summaryGenerator;
    private readonly IContentHasher _contentHasher;
    private readonly IndexingOptions _options;
    private readonly ILogger<SolutionCodeIndexer> _logger;

    public SolutionCodeIndexer(
        IEmbeddingProvider embeddingProvider,
        IVectorStore vectorStore,
        ICodeSummaryGenerator summaryGenerator,
        IContentHasher contentHasher,
        IOptions<IndexingOptions> options,
        ILogger<SolutionCodeIndexer> logger)
    {
        _embeddingProvider = embeddingProvider;
        _vectorStore = vectorStore;
        _summaryGenerator = summaryGenerator;
        _contentHasher = contentHasher;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IndexResult> IndexAsync(IndexRequest request, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var errors = new List<string>();
        var created = 0;
        var updated = 0;
        var filesScanned = 0;
        var filesIndexed = 0;
        var chunksToUpsert = new List<CodeChunk>();
        var vectorIdsToDelete = new HashSet<string>(StringComparer.Ordinal);
        var currentManifest = new IndexManifest();
        var currentDocumentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.SolutionPath) || !File.Exists(request.SolutionPath))
        {
            return new IndexResult
            {
                Status = IndexStatus.Failed,
                Errors = [$"Solution path not found: {request.SolutionPath}"],
            };
        }

        RoslynBootstrapper.EnsureRegistered();

        var manifestStore = new IndexManifestStore(_options);
        var previousManifest = request.ReindexMode == ReindexMode.ChangedOnly
            ? await manifestStore.LoadAsync(request.RepoName, cancellationToken).ConfigureAwait(false)
            : new IndexManifest();

        using var workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(eventArgs => _logger.LogWarning("MSBuild workspace warning: {Message}", eventArgs.Diagnostic.Message));

        Solution solution;
        try
        {
            solution = await workspace.OpenSolutionAsync(request.SolutionPath, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new IndexResult
            {
                Status = IndexStatus.Failed,
                Errors = [$"Failed to open solution '{request.SolutionPath}': {ex.Message}"],
            };
        }

        if (request.ReindexMode == ReindexMode.Full)
        {
            await _vectorStore.DeleteByRepoAsync(request.RepoName, cancellationToken).ConfigureAwait(false);
        }

        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!project.SupportsCompilation || !string.Equals(project.Language, LanguageNames.CSharp, StringComparison.Ordinal))
            {
                continue;
            }

            Compilation? compilation;
            try
            {
                compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add($"Project compilation failed for {project.Name}: {ex.Message}");
                continue;
            }

            if (compilation is null)
            {
                continue;
            }

            foreach (var document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (RoslynTextHelpers.IsGeneratedFile(document.FilePath))
                {
                    continue;
                }

                if (!string.Equals(document.Project.Language, LanguageNames.CSharp, StringComparison.Ordinal))
                {
                    continue;
                }

                filesScanned++;

                try
                {
                    var documentPath = document.FilePath ?? string.Empty;
                    var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                    var documentHash = _contentHasher.ComputeHash(sourceText.ToString());
                    currentDocumentPaths.Add(documentPath);

                    if (request.ReindexMode == ReindexMode.ChangedOnly &&
                        previousManifest.Documents.TryGetValue(documentPath, out var previousDocument) &&
                        string.Equals(previousDocument.ContentHash, documentHash, StringComparison.Ordinal))
                    {
                        currentManifest.Documents[documentPath] = previousDocument;
                        continue;
                    }

                    var chunks = await ExtractChunksAsync(request, project, document, sourceText, cancellationToken).ConfigureAwait(false);
                    var chunkIds = chunks.Select(chunk => chunk.Id).ToHashSet(StringComparer.Ordinal);
                    var previousDocumentManifest = previousManifest.Documents.TryGetValue(documentPath, out var existingDocumentManifest)
                        ? existingDocumentManifest
                        : null;

                    if (request.ReindexMode == ReindexMode.ChangedOnly && previousDocumentManifest is not null)
                    {
                        foreach (var staleChunkId in previousDocumentManifest.ChunkIds.Where(chunkId => !chunkIds.Contains(chunkId)))
                        {
                            vectorIdsToDelete.Add(staleChunkId);
                        }
                    }

                    if (chunks.Count == 0)
                    {
                        if (previousDocumentManifest is not null)
                        {
                            filesIndexed++;
                        }

                        currentManifest.Documents[documentPath] = new DocumentManifest
                        {
                            ContentHash = documentHash,
                            ProjectName = project.Name,
                            FilePath = documentPath,
                            ChunkCount = 0,
                            ChunkIds = [],
                            SymbolKinds = [],
                            LastIndexedUtc = DateTimeOffset.UtcNow,
                        };

                        continue;
                    }

                    var documentHadUpserts = false;
                    foreach (var chunk in chunks)
                    {
                        documentHadUpserts = true;

                        if (previousDocumentManifest is not null && previousDocumentManifest.ChunkIds.Contains(chunk.Id, StringComparer.Ordinal))
                        {
                            updated++;
                        }
                        else
                        {
                            created++;
                        }

                        chunksToUpsert.Add(chunk);
                    }

                    currentManifest.Documents[documentPath] = new DocumentManifest
                    {
                        ContentHash = documentHash,
                        ProjectName = project.Name,
                        FilePath = documentPath,
                        ChunkCount = chunks.Count,
                        ChunkIds = chunkIds.OrderBy(id => id, StringComparer.Ordinal).ToList(),
                        SymbolKinds = chunks
                            .Select(chunk => chunk.SymbolKind.ToString())
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(kind => kind, StringComparer.Ordinal)
                            .ToArray(),
                        LastIndexedUtc = DateTimeOffset.UtcNow,
                    };

                    if (documentHadUpserts)
                    {
                        filesIndexed++;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Document failed for {document.FilePath}: {ex.Message}");
                }
            }
        }

        if (request.ReindexMode == ReindexMode.ChangedOnly)
        {
            foreach (var removedDocumentPath in previousManifest.Documents.Keys.Where(path => !currentDocumentPaths.Contains(path)))
            {
                if (previousManifest.Documents.TryGetValue(removedDocumentPath, out var removedDocument))
                {
                    foreach (var chunkId in removedDocument.ChunkIds)
                    {
                        vectorIdsToDelete.Add(chunkId);
                    }
                }
            }
        }

        if (chunksToUpsert.Count > 0)
        {
            var records = new List<VectorRecord>(chunksToUpsert.Count);
            foreach (var chunk in chunksToUpsert)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var vector = await _embeddingProvider.CreateEmbeddingAsync(BuildEmbeddingInput(chunk), cancellationToken).ConfigureAwait(false);
                records.Add(new VectorRecord
                {
                    Id = chunk.Id,
                    Vector = vector,
                    Payload = BuildPayload(chunk),
                });
            }

            try
            {
                await _vectorStore.UpsertAsync(records, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add($"Vector store write failed: {ex.Message}");
            }
        }

        if (vectorIdsToDelete.Count > 0)
        {
            try
            {
                await _vectorStore.DeleteByIdsAsync(vectorIdsToDelete.ToArray(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add($"Vector delete failed: {ex.Message}");
            }
        }

        if (errors.Count == 0)
        {
            try
            {
                await manifestStore.SaveAsync(request.RepoName, currentManifest, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add($"Manifest save failed: {ex.Message}");
            }
        }

        stopwatch.Stop();

        return new IndexResult
        {
            Status = errors.Count == 0 ? IndexStatus.Completed : IndexStatus.CompletedWithErrors,
            FilesScanned = filesScanned,
            FilesIndexed = filesIndexed,
            ChunksCreated = created,
            ChunksUpdated = updated,
            DurationMs = stopwatch.ElapsedMilliseconds,
            Errors = errors,
        };
    }

    private async Task<IReadOnlyList<CodeChunk>> ExtractChunksAsync(
        IndexRequest request,
        Project project,
        Document document,
        SourceText sourceText,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return Array.Empty<CodeChunk>();
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (semanticModel is null)
        {
            return Array.Empty<CodeChunk>();
        }

        var chunks = new List<CodeChunk>();

        foreach (var node in root.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (node is not (ClassDeclarationSyntax or InterfaceDeclarationSyntax or RecordDeclarationSyntax or MethodDeclarationSyntax or ConstructorDeclarationSyntax or PropertyDeclarationSyntax))
            {
                continue;
            }

            if (semanticModel.GetDeclaredSymbol(node, cancellationToken) is not ISymbol symbol)
            {
                continue;
            }

            var controllerActionInfo = TryGetControllerActionInfo(node, symbol);
            var kind = symbol.ToCodeSymbolKind(controllerActionInfo.IsControllerAction);
            if (kind == CodeSymbolKind.NamedType)
            {
                continue;
            }

            chunks.Add(CreateChunk(request, project, document, node, semanticModel, sourceText, symbol, kind, controllerActionInfo));
        }

        return chunks;
    }

    private CodeChunk CreateChunk(
        IndexRequest request,
        Project project,
        Document document,
        SyntaxNode node,
        SemanticModel semanticModel,
        SourceText sourceText,
        ISymbol symbol,
        CodeSymbolKind kind,
        ControllerActionInfo controllerActionInfo)
    {
        var declarationSpan = node.Span;
        var startLine = RoslynTextHelpers.GetStartLine(node.SyntaxTree, declarationSpan);
        var endLine = RoslynTextHelpers.GetEndLine(node.SyntaxTree, declarationSpan);
        var snippet = RoslynTextHelpers.Truncate(RoslynTextHelpers.NormalizeSnippet(sourceText.GetSubText(declarationSpan).ToString()), _options.SnippetLength);
        var attributes = symbol.GetAttributes()
            .Select(GetAttributeDisplayName)
            .Where(attribute => !string.IsNullOrWhiteSpace(attribute))
            .Select(attribute => attribute!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var dependencies = ExtractDependencies(node, semanticModel, symbol).ToArray();
        var chunk = new CodeChunk
        {
            Id = string.Empty,
            RepoName = request.RepoName,
            CommitSha = request.CommitSha,
            SolutionPath = request.SolutionPath,
            ProjectName = project.Name,
            FilePath = document.FilePath ?? string.Empty,
            SymbolId = symbol.GetStableSymbolId(),
            SymbolKind = kind,
            SymbolName = symbol.GetDisplayName(),
            Namespace = symbol.GetNamespaceName(),
            ContainingType = symbol.GetContainingTypeName(),
            Signature = SignatureBuilder.Build(symbol, kind),
            Attributes = attributes,
            Dependencies = dependencies,
            Visibility = symbol.ToVisibility(),
            StartLine = startLine,
            EndLine = endLine,
            ChunkText = string.Empty,
            Summary = string.Empty,
            ContentHash = string.Empty,
            SourceSnippet = snippet,
            RouteTemplate = controllerActionInfo.RouteTemplate,
            HttpVerb = controllerActionInfo.HttpVerb,
            ControllerName = controllerActionInfo.ControllerName,
            IsApiController = controllerActionInfo.IsApiController,
            XmlDocumentation = symbol.GetDocumentationCommentXml(),
        };

        var summary = _summaryGenerator.GenerateSummary(chunk);
        chunk = chunk with { Summary = summary };
        var chunkText = ChunkTextBuilder.Build(chunk);
        var contentHash = _contentHasher.ComputeHash(chunkText);
        var id = $"{request.RepoName}:{chunk.SymbolId}";

        return chunk with
        {
            Id = id,
            ChunkText = chunkText,
            ContentHash = contentHash,
        };
    }

    private IEnumerable<string> ExtractDependencies(SyntaxNode node, SemanticModel semanticModel, ISymbol symbol)
    {
        var dependencies = new HashSet<string>(StringComparer.Ordinal);

        if (symbol is INamedTypeSymbol namedType)
        {
            if (namedType.BaseType is not null && namedType.BaseType.SpecialType != SpecialType.System_Object)
            {
                dependencies.Add(namedType.BaseType.Name);
            }

            foreach (var @interface in namedType.Interfaces)
            {
                dependencies.Add(@interface.Name);
            }
        }

        foreach (var identifier in node.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var resolved = semanticModel.GetSymbolInfo(identifier).Symbol;
            if (resolved is null || SymbolEqualityComparer.Default.Equals(resolved, symbol))
            {
                continue;
            }

            if (resolved is ILocalSymbol or IParameterSymbol)
            {
                continue;
            }

            dependencies.Add(resolved.Name);
        }

        if (symbol is IMethodSymbol methodSymbol)
        {
            foreach (var parameter in methodSymbol.Parameters)
            {
                dependencies.Add(parameter.Type.Name);
            }

            if (methodSymbol.ReturnType is INamedTypeSymbol returnType && returnType.SpecialType != SpecialType.System_Void)
            {
                dependencies.Add(returnType.Name);
            }
        }

        return dependencies.Where(name => !string.IsNullOrWhiteSpace(name)).OrderBy(name => name, StringComparer.Ordinal);
    }

    private static ControllerActionInfo TryGetControllerActionInfo(SyntaxNode node, ISymbol symbol)
    {
        if (symbol is not IMethodSymbol methodSymbol)
        {
            return ControllerActionInfo.None;
        }

        var containingType = methodSymbol.ContainingType;
        var isController = containingType is not null &&
                           (containingType.Name.EndsWith("Controller", StringComparison.Ordinal) ||
                            containingType.BaseTypesAndSelf().Any(type => type.Name == "ControllerBase"));
        var controllerName = containingType?.Name ?? string.Empty;
        var isApiController = containingType is not null &&
                               (containingType.GetAttributes().Any(attribute => string.Equals(attribute.AttributeClass?.Name, "ApiControllerAttribute", StringComparison.OrdinalIgnoreCase)) ||
                                containingType.BaseTypesAndSelf().Any(type => type.Name == "ControllerBase"));

        var hasRouteAttributeSyntax = node is MethodDeclarationSyntax methodSyntax &&
            methodSyntax.AttributeLists
                .SelectMany(attributeList => attributeList.Attributes)
                .Any(attribute =>
                {
                    var name = attribute.Name.ToString();
                    return name.StartsWith("Http", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(name, "Route", StringComparison.OrdinalIgnoreCase) ||
                           name.EndsWith("Route", StringComparison.OrdinalIgnoreCase);
                });

        var routeAttributes = methodSymbol.GetAttributes()
            .Where(attribute =>
            {
                var name = attribute.AttributeClass?.Name ?? string.Empty;
                return name.StartsWith("Http", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "RouteAttribute", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();

        var controllerRouteAttributes = containingType?.GetAttributes()
            .Where(attribute => string.Equals(attribute.AttributeClass?.Name, "RouteAttribute", StringComparison.OrdinalIgnoreCase))
            .ToArray() ?? Array.Empty<AttributeData>();

        if (!isController || (!hasRouteAttributeSyntax && routeAttributes.Length == 0))
        {
            return ControllerActionInfo.None;
        }

        var httpVerb = routeAttributes
            .Select(attribute => attribute.AttributeClass?.Name)
            .FirstOrDefault(name => name is not null && name.StartsWith("Http", StringComparison.OrdinalIgnoreCase));
        httpVerb = httpVerb?.Replace("Attribute", string.Empty, StringComparison.OrdinalIgnoreCase);

        var controllerRouteTemplate = controllerRouteAttributes.Select(GetAttributeRoute).FirstOrDefault(route => !string.IsNullOrWhiteSpace(route));
        var actionRouteTemplate = routeAttributes.Select(GetAttributeRoute).FirstOrDefault(route => !string.IsNullOrWhiteSpace(route));
        var routeTemplate = CombineRouteTemplates(controllerRouteTemplate, actionRouteTemplate, controllerName);
        return new ControllerActionInfo(true, routeTemplate, httpVerb, controllerName, isApiController);
    }

    private static string? CombineRouteTemplates(string? controllerRouteTemplate, string? actionRouteTemplate, string controllerName)
    {
        var normalizedControllerName = controllerName.EndsWith("Controller", StringComparison.Ordinal)
            ? controllerName[..^"Controller".Length]
            : controllerName;

        static string ReplaceControllerToken(string? template, string controller)
        {
            if (string.IsNullOrWhiteSpace(template))
            {
                return string.Empty;
            }

            return template.Replace("[controller]", controller.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
        }

        var controllerRoute = ReplaceControllerToken(controllerRouteTemplate, normalizedControllerName);
        var actionRoute = ReplaceControllerToken(actionRouteTemplate, normalizedControllerName);

        if (string.IsNullOrWhiteSpace(controllerRoute))
        {
            return actionRoute;
        }

        if (string.IsNullOrWhiteSpace(actionRoute))
        {
            return controllerRoute;
        }

        return $"{controllerRoute.TrimEnd('/')}/{actionRoute.TrimStart('/')}";
    }

    private static string? GetAttributeRoute(AttributeData attributeData)
    {
        if (attributeData.ConstructorArguments.Length > 0)
        {
            var value = attributeData.ConstructorArguments[0].Value?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        foreach (var namedArgument in attributeData.NamedArguments)
        {
            if (string.Equals(namedArgument.Key, "Template", StringComparison.OrdinalIgnoreCase) &&
                namedArgument.Value.Value is string template &&
                !string.IsNullOrWhiteSpace(template))
            {
                return template;
            }
        }

        return null;
    }

    private static string GetAttributeDisplayName(AttributeData attributeData)
    {
        return attributeData.AttributeClass?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            ?? attributeData.AttributeClass?.Name
            ?? attributeData.ToString()
            ?? string.Empty;
    }

    private static string BuildEmbeddingInput(CodeChunk chunk)
    {
        return string.Join('\n', new[]
        {
            chunk.SymbolName,
            chunk.Signature,
            chunk.Summary,
            chunk.SourceSnippet ?? string.Empty,
        });
    }

    private static IReadOnlyDictionary<string, object?> BuildPayload(CodeChunk chunk)
    {
        return new Dictionary<string, object?>
        {
            ["repoName"] = chunk.RepoName,
            ["commitSha"] = chunk.CommitSha,
            ["solutionPath"] = chunk.SolutionPath,
            ["projectName"] = chunk.ProjectName,
            ["filePath"] = chunk.FilePath,
            ["symbolId"] = chunk.SymbolId,
            ["symbolKind"] = chunk.SymbolKind.ToString(),
            ["symbolName"] = chunk.SymbolName,
            ["namespace"] = chunk.Namespace,
            ["containingType"] = chunk.ContainingType,
            ["signature"] = chunk.Signature,
            ["attributes"] = chunk.Attributes,
            ["dependencies"] = chunk.Dependencies,
            ["visibility"] = chunk.Visibility.ToString(),
            ["startLine"] = chunk.StartLine,
            ["endLine"] = chunk.EndLine,
            ["summary"] = chunk.Summary,
            ["contentHash"] = chunk.ContentHash,
            ["chunkText"] = chunk.ChunkText,
            ["routeTemplate"] = chunk.RouteTemplate,
            ["httpVerb"] = chunk.HttpVerb,
            ["controllerName"] = chunk.ControllerName,
            ["isApiController"] = chunk.IsApiController,
            ["snippet"] = chunk.SourceSnippet,
        };
    }

    private sealed record ControllerActionInfo(bool IsControllerAction, string? RouteTemplate, string? HttpVerb, string? ControllerName, bool IsApiController)
    {
        public static readonly ControllerActionInfo None = new(false, null, null, null, false);
    }
}
