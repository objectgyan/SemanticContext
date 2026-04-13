using Microsoft.CodeAnalysis;
using SemanticContext.Contracts;

namespace SemanticContext.Indexer;

internal static class SignatureBuilder
{
    public static string Build(ISymbol symbol, CodeSymbolKind kind)
    {
        return kind switch
        {
            CodeSymbolKind.Method => BuildMethod((IMethodSymbol)symbol),
            CodeSymbolKind.ControllerAction => BuildMethod((IMethodSymbol)symbol),
            CodeSymbolKind.Constructor => BuildConstructor((IMethodSymbol)symbol),
            CodeSymbolKind.Property => BuildProperty((IPropertySymbol)symbol),
            CodeSymbolKind.Class or CodeSymbolKind.Interface or CodeSymbolKind.Record or CodeSymbolKind.NamedType => BuildType((INamedTypeSymbol)symbol),
            _ => symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        };
    }

    private static string BuildType(INamedTypeSymbol symbol)
    {
        var kind = symbol.TypeKind switch
        {
            TypeKind.Interface => "interface",
            TypeKind.Class when symbol.IsRecord => "record",
            TypeKind.Class => "class",
            TypeKind.Struct => "struct",
            _ => symbol.TypeKind.ToString().ToLowerInvariant(),
        };

        var name = symbol.ToDisplayString(new SymbolDisplayFormat(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            memberOptions: SymbolDisplayMemberOptions.None,
            parameterOptions: SymbolDisplayParameterOptions.None,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers));

        var bases = symbol.BaseTypesAndSelf()
            .Skip(1)
            .Where(baseType => baseType.SpecialType != SpecialType.System_Object && baseType.TypeKind != TypeKind.Delegate)
            .Select(baseType => baseType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var baseClause = bases.Count == 0 ? string.Empty : " : " + string.Join(", ", bases);
        return $"{kind} {name}{baseClause}".Trim();
    }

    private static string BuildMethod(IMethodSymbol symbol)
    {
        var modifiers = new List<string>();
        if (symbol.IsStatic)
        {
            modifiers.Add("static");
        }

        if (symbol.IsAsync)
        {
            modifiers.Add("async");
        }

        var returnType = symbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var parameters = string.Join(", ", symbol.Parameters.Select(parameter =>
            $"{parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {parameter.Name}"));
        var prefix = modifiers.Count == 0 ? string.Empty : string.Join(" ", modifiers) + " ";
        var typeParameters = symbol.TypeParameters.Length == 0
            ? string.Empty
            : "<" + string.Join(", ", symbol.TypeParameters.Select(typeParameter => typeParameter.Name)) + ">";

        return $"{prefix}{returnType} {symbol.Name}{typeParameters}({parameters})";
    }

    private static string BuildConstructor(IMethodSymbol symbol)
    {
        var parameters = string.Join(", ", symbol.Parameters.Select(parameter =>
            $"{parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {parameter.Name}"));
        return $"{symbol.ContainingType?.Name ?? symbol.Name}({parameters})";
    }

    private static string BuildProperty(IPropertySymbol symbol)
    {
        var accessors = new List<string>();
        if (symbol.GetMethod is not null)
        {
            accessors.Add("get");
        }

        if (symbol.SetMethod is not null)
        {
            accessors.Add("set");
        }

        var accessorText = accessors.Count == 0 ? string.Empty : " {" + string.Join("; ", accessors) + "}";
        return $"{symbol.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {symbol.Name}{accessorText}";
    }
}

