using System.Runtime.InteropServices;
using TreeSitter;

namespace Semble.Index;

/// <summary>
/// Tree-sitter-backed code-aware chunker, parameterised by the grammar pointer
/// returned by the corresponding `tree_sitter_<lang>()` C entry point. Walks the
/// parse tree, collects splittable nodes (per-language whitelist), and
/// greedy-merges consecutive nodes up to <see cref="DefaultChunkSize"/>
/// characters per chunk — the same shape as <see cref="RoslynChunker"/>.
/// </summary>
public static class TreeSitterChunker
{
    public const int DefaultChunkSize = 1500;

    /// <summary>
    /// Chunk <paramref name="source"/> using the supplied tree-sitter grammar.
    /// Returns null when the parser surfaces no splittable nodes (e.g. a file
    /// of pure comments or whitespace-only directives) so the caller can fall
    /// back to line-based chunking.
    /// </summary>
    public static List<Chunk>? TryChunk(
        string source,
        string filePath,
        string? language,
        IntPtr grammar,
        IReadOnlySet<string> splittableKinds,
        int chunkSize = DefaultChunkSize)
    {
        if (grammar == IntPtr.Zero)
            return null;

        using var parser = new Parser { Language = new Language(grammar) };
        using var tree = parser.Parse(source);
        var root = tree.Root;

        var splittable = new List<Node>();
        Collect(root, splittable, splittableKinds, chunkSize);
        if (splittable.Count == 0)
            return null;

        var chunks = new List<Chunk>();
        int chunkStart = -1;
        int chunkEnd = -1;
        foreach (var node in splittable)
        {
            int s = (int)node.StartByte;
            int e = (int)node.EndByte;
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

    private static void Collect(
        Node node,
        List<Node> result,
        IReadOnlySet<string> splittableKinds,
        int chunkSize)
    {
        foreach (var child in node.Children)
        {
            if (!child.IsNamed)
                continue;
            if (splittableKinds.Contains(child.Kind))
            {
                int size = (int)(child.EndByte - child.StartByte);
                if (size <= chunkSize)
                {
                    result.Add(child);
                }
                else
                {
                    int before = result.Count;
                    Collect(child, result, splittableKinds, chunkSize);
                    if (result.Count == before)
                        result.Add(child); // oversized leaf — emit anyway
                }
            }
            else
            {
                // Container we don't split on (e.g. translation_unit) —
                // descend looking for splittable nodes inside.
                Collect(child, result, splittableKinds, chunkSize);
            }
        }
    }

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

/// <summary>
/// Per-language tree-sitter grammar bindings. Each grammar lives in its own
/// `libtree-sitter-<lang>` native library shipped by the corresponding NuGet,
/// resolved at runtime via P/Invoke.
/// </summary>
public static class TreeSitterGrammars
{
    public static IntPtr Cpp => CppNative.tree_sitter_cpp();

    /// <summary>
    /// Whitelist of node kinds that the cpp chunker treats as splittable units.
    /// Tree-sitter-cpp emits these names; non-named anonymous nodes (`;`, `{`)
    /// are filtered out by <see cref="Node.IsNamed"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> CppSplittableKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "function_definition",
        "class_specifier",
        "struct_specifier",
        "union_specifier",
        "enum_specifier",
        "namespace_definition",
        "template_declaration",
        "linkage_specification",   // extern "C" { ... }
        "type_definition",         // typedef
        "alias_declaration",       // using X = Y;
        "using_declaration",
        "declaration",             // top-level variable / forward decls
        "preproc_include",
        "preproc_def",
        "preproc_function_def",
        "preproc_call",
    };

    private static class CppNative
    {
        [DllImport("tree-sitter-cpp")]
        internal static extern IntPtr tree_sitter_cpp();
    }
}
