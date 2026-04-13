using System.Text;
using SemanticContext.Contracts;

namespace SemanticContext.Indexer;

public sealed class DeterministicCodeSummaryGenerator : ICodeSummaryGenerator
{
    public string GenerateSummary(CodeChunk chunk)
    {
        if (!string.IsNullOrWhiteSpace(chunk.XmlDocumentation))
        {
            var xmlSummary = ExtractXmlSummary(chunk.XmlDocumentation);
            if (!string.IsNullOrWhiteSpace(xmlSummary))
            {
                return xmlSummary;
            }
        }

        return chunk.SymbolKind switch
        {
            CodeSymbolKind.ControllerAction => DescribeControllerAction(chunk),
            CodeSymbolKind.Constructor => $"Constructor for {chunk.ContainingType}.",
            CodeSymbolKind.Property => DescribeProperty(chunk),
            CodeSymbolKind.Method => DescribeMethod(chunk),
            CodeSymbolKind.Class => DescribeType("Class", chunk),
            CodeSymbolKind.Interface => DescribeType("Interface", chunk),
            CodeSymbolKind.Record => DescribeType("Record", chunk),
            _ => DescribeMethod(chunk),
        };
    }

    private static string DescribeControllerAction(CodeChunk chunk)
    {
        var verb = chunk.HttpVerb?.ToLowerInvariant() switch
        {
            "get" => "returns",
            "post" => "creates",
            "put" => "updates",
            "delete" => "deletes",
            "patch" => "patches",
            _ => "handles",
        };

        var route = string.IsNullOrWhiteSpace(chunk.RouteTemplate) ? "a controller route" : $"route {chunk.RouteTemplate}";
        return $"ASP.NET controller action that {verb} {route}.";
    }

    private static string DescribeProperty(CodeChunk chunk)
    {
        var nameTokens = SplitPascalCase(chunk.SymbolName);
        return nameTokens.Count > 0
            ? $"Property exposing {string.Join(" ", nameTokens)}."
            : $"Property on {chunk.ContainingType}.";
    }

    private static string DescribeMethod(CodeChunk chunk)
    {
        var nameTokens = SplitPascalCase(chunk.SymbolName);
        if (nameTokens.Count == 0)
        {
            return $"Method on {chunk.ContainingType}.";
        }

        var action = nameTokens[0];
        var remainder = nameTokens.Skip(1).ToList();
        return remainder.Count > 0
            ? $"{action} {string.Join(" ", remainder)}."
            : $"{action} logic for {chunk.ContainingType}.";
    }

    private static string DescribeType(string noun, CodeChunk chunk)
    {
        if (chunk.Dependencies.Count == 0)
        {
            return $"{noun} representing {chunk.SymbolName}.";
        }

        return $"{noun} representing {chunk.SymbolName} and related {string.Join(", ", chunk.Dependencies.Take(3))}.";
    }

    private static string ExtractXmlSummary(string xml)
    {
        var start = xml.IndexOf("<summary>", StringComparison.OrdinalIgnoreCase);
        var end = xml.IndexOf("</summary>", StringComparison.OrdinalIgnoreCase);
        if (start < 0 || end <= start)
        {
            return string.Empty;
        }

        var raw = xml[(start + "<summary>".Length)..end];
        return string.Join(" ", raw.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static List<string> SplitPascalCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var tokens = new List<string>();
        var current = new StringBuilder();

        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch is '_' or '-')
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            if (i > 0 && char.IsUpper(ch) && current.Length > 0 && char.IsLower(current[^1]))
            {
                tokens.Add(current.ToString());
                current.Clear();
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens.Where(token => !string.IsNullOrWhiteSpace(token)).ToList();
    }
}

