using System.Text.RegularExpressions;

namespace Semble;

/// <summary>
/// Identifier tokenisation for BM25 indexing. Mirrors src/semble/tokens.py.
/// Compound identifiers (camelCase, PascalCase, snake_case) are expanded into
/// sub-tokens so partial matches work; the original compound token is also
/// preserved (lower-cased) for exact-match boosting.
/// </summary>
public static class Tokens
{
    private static readonly Regex TokenRe = new(
        @"[a-zA-Z_][a-zA-Z0-9_]*",
        RegexOptions.Compiled);

    // Split on camelCase/PascalCase boundaries:
    //   "HandlerStack"    -> ["Handler", "Stack"]
    //   "getHTTPResponse" -> ["get", "HTTP", "Response"]
    //   "XMLParser"       -> ["XML", "Parser"]
    private static readonly Regex CamelRe = new(
        @"[A-Z]+(?=[A-Z][a-z])|[A-Z]?[a-z]+|[A-Z]+|[0-9]+",
        RegexOptions.Compiled);

    /// <summary>
    /// Split a single identifier into sub-tokens via camelCase/snake_case.
    /// Returns the original token (lower-cased) plus any sub-tokens; if there
    /// is only one part the result is just the lower-cased original.
    /// </summary>
    public static List<string> SplitIdentifier(string token)
    {
        var lower = token.ToLowerInvariant();
        List<string> parts;

        if (token.Contains('_'))
        {
            parts = new List<string>();
            foreach (var p in lower.Split('_'))
            {
                if (p.Length > 0)
                    parts.Add(p);
            }
        }
        else
        {
            parts = new List<string>();
            foreach (Match m in CamelRe.Matches(token))
            {
                parts.Add(m.Value.ToLowerInvariant());
            }
        }

        if (parts.Count >= 2)
        {
            var result = new List<string>(parts.Count + 1) { lower };
            result.AddRange(parts);
            return result;
        }
        return new List<string> { lower };
    }

    /// <summary>
    /// Split text into lower-case identifier-like tokens for BM25 indexing.
    /// </summary>
    public static List<string> Tokenize(string text)
    {
        var result = new List<string>();
        foreach (Match m in TokenRe.Matches(text))
        {
            result.AddRange(SplitIdentifier(m.Value));
        }
        return result;
    }
}
