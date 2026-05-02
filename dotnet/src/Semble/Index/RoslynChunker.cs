using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Semble.Index;

/// <summary>
/// Roslyn-backed C# chunker. Walks the syntax tree, collects splittable nodes
/// (namespaces, types, methods, etc.) and greedy-merges consecutive nodes up to
/// <see cref="DefaultChunkSize"/> characters per chunk. Mirrors the upstream
/// chonkie CodeChunker behaviour for C# specifically — character offsets are
/// converted to 1-indexed line numbers the same way as in src/semble/index/chunker.py.
/// </summary>
public static class RoslynChunker
{
    public const int DefaultChunkSize = 1500;

    /// <summary>
    /// Chunk a C# source. Returns null when the parser can't surface any
    /// splittable members (e.g. file is just `using` directives or top-level
    /// statements) so the caller can fall back to line-based chunking.
    /// </summary>
    public static List<Chunk>? TryChunkCSharp(
        string source,
        string filePath,
        string? language,
        int chunkSize = DefaultChunkSize)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();

        var splittable = new List<SyntaxNode>();
        CollectSplittableNodes(root, splittable, chunkSize);
        if (splittable.Count == 0)
            return null;

        var chunks = new List<Chunk>();
        int chunkStart = -1;
        int chunkEnd = -1;
        foreach (var node in splittable)
        {
            int s = node.SpanStart;
            int e = node.Span.End;
            if (e <= s)
                continue;

            if (chunkStart < 0)
            {
                chunkStart = s;
                chunkEnd = e;
            }
            else if (e - chunkStart <= chunkSize)
            {
                chunkEnd = e;
            }
            else
            {
                chunks.Add(MakeChunk(source, filePath, language, chunkStart, chunkEnd));
                chunkStart = s;
                chunkEnd = e;
            }
        }
        if (chunkStart >= 0)
            chunks.Add(MakeChunk(source, filePath, language, chunkStart, chunkEnd));

        return chunks.Count == 0 ? null : chunks;
    }

    private static void CollectSplittableNodes(SyntaxNode node, List<SyntaxNode> result, int chunkSize)
    {
        var members = GetMembers(node).ToList();
        if (members.Count == 0)
        {
            // Leaf — but skip the synthetic compilation-unit root with zero members.
            if (node is not CompilationUnitSyntax)
                result.Add(node);
            return;
        }

        foreach (var m in members)
        {
            if (m.Span.Length <= chunkSize)
                result.Add(m);
            else if (HasMembers(m))
                CollectSplittableNodes(m, result, chunkSize);
            else
                result.Add(m); // single member that exceeds the budget; emit as-is
        }
    }

    private static IEnumerable<SyntaxNode> GetMembers(SyntaxNode node) => node switch
    {
        CompilationUnitSyntax c => c.Members,
        BaseNamespaceDeclarationSyntax n => n.Members,
        TypeDeclarationSyntax t => t.Members,
        _ => Enumerable.Empty<SyntaxNode>(),
    };

    private static bool HasMembers(SyntaxNode node) =>
        node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax;

    private static Chunk MakeChunk(string source, string filePath, string? language, int startCh, int endCh)
    {
        var content = source.Substring(startCh, endCh - startCh);
        // 1-indexed line numbers, matching src/semble/index/chunker.py:_chunk_with_chonkie:
        //   start_line = source[:start_index].count("\n") + 1
        //   end_line   = source[:max(end_index - 1, start_index)].count("\n") + 1
        int startLine = CountNewlines(source, 0, startCh) + 1;
        int endIdx = Math.Max(endCh - 1, startCh);
        int endLine = CountNewlines(source, 0, endIdx) + 1;
        return new Chunk(content, filePath, startLine, endLine, language);
    }

    private static int CountNewlines(string source, int start, int end)
    {
        int n = 0;
        for (int i = start; i < end && i < source.Length; i++)
            if (source[i] == '\n') n++;
        return n;
    }
}
