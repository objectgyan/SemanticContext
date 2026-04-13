using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using SemanticContext.Contracts;

namespace SemanticContext.Indexer;

internal static class RoslynSymbolExtensions
{
    public static CodeSymbolKind ToCodeSymbolKind(this ISymbol symbol, bool controllerAction)
    {
        if (controllerAction)
        {
            return CodeSymbolKind.ControllerAction;
        }

        return symbol switch
        {
            IMethodSymbol { MethodKind: MethodKind.Constructor } => CodeSymbolKind.Constructor,
            IMethodSymbol => CodeSymbolKind.Method,
            IPropertySymbol => CodeSymbolKind.Property,
            INamedTypeSymbol { TypeKind: TypeKind.Interface } => CodeSymbolKind.Interface,
            INamedTypeSymbol { TypeKind: TypeKind.Class, IsRecord: true } => CodeSymbolKind.Record,
            INamedTypeSymbol { TypeKind: TypeKind.Struct } => CodeSymbolKind.Record,
            INamedTypeSymbol { TypeKind: TypeKind.Class } => CodeSymbolKind.Class,
            _ => CodeSymbolKind.NamedType,
        };
    }

    public static SymbolVisibility ToVisibility(this ISymbol symbol)
    {
        return symbol.DeclaredAccessibility switch
        {
            Accessibility.Public => SymbolVisibility.Public,
            Accessibility.Internal => SymbolVisibility.Internal,
            Accessibility.Protected => SymbolVisibility.Protected,
            Accessibility.Private => SymbolVisibility.Private,
            Accessibility.ProtectedAndInternal => SymbolVisibility.ProtectedInternal,
            Accessibility.ProtectedOrInternal => SymbolVisibility.ProtectedInternal,
            _ => SymbolVisibility.Unknown,
        };
    }

    public static string GetStableSymbolId(this ISymbol symbol)
    {
        return symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    public static string GetDisplayName(this ISymbol symbol)
    {
        return symbol.Name;
    }

    public static string GetContainingTypeName(this ISymbol symbol)
    {
        return symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? string.Empty;
    }

    public static string GetNamespaceName(this ISymbol symbol)
    {
        var ns = symbol.ContainingNamespace;
        return ns is null || ns.IsGlobalNamespace
            ? string.Empty
            : ns.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }
}

internal static class RoslynTypeExtensions
{
    public static IEnumerable<INamedTypeSymbol> BaseTypesAndSelf(this INamedTypeSymbol symbol)
    {
        for (var current = symbol; current is not null; current = current.BaseType)
        {
            yield return current;
        }
    }
}

internal static class RoslynTextHelpers
{
    public static bool IsGeneratedFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return true;
        }

        var normalized = filePath.Replace('\\', '/');
        if (normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var fileName = Path.GetFileName(normalized);
        return fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase);
    }

    public static int GetStartLine(SyntaxTree syntaxTree, TextSpan span)
    {
        return syntaxTree.GetLineSpan(span).StartLinePosition.Line + 1;
    }

    public static int GetEndLine(SyntaxTree syntaxTree, TextSpan span)
    {
        return syntaxTree.GetLineSpan(span).EndLinePosition.Line + 1;
    }

    public static string NormalizeSnippet(string text)
    {
        return string.Join(
            '\n',
            text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength].TrimEnd();
    }
}
