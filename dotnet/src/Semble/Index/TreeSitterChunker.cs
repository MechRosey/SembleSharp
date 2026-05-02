using System.Text;
using static Semble.Index.TreeSitterInterop;

namespace Semble.Index;

/// <summary>
/// Tree-sitter-backed code-aware chunker, parameterised by the grammar pointer
/// returned by the corresponding `tree_sitter_<lang>()` C entry point. Walks the
/// parse tree, collects splittable nodes (per-language whitelist), and
/// greedy-merges consecutive nodes up to <see cref="DefaultChunkSize"/>
/// characters per chunk — same shape as <see cref="RoslynChunker"/>.
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

        // Encode source to UTF-8 once. tree-sitter operates on UTF-8 bytes;
        // .NET strings are UTF-16, so the byte offsets returned by tree-sitter
        // must be mapped back to .NET string indices via the byte→char map
        // computed below.
        var utf8 = Encoding.UTF8.GetBytes(source);
        if (utf8.Length == 0)
            return null;

        var parser = ts_parser_new();
        if (parser == IntPtr.Zero)
            return null;
        try
        {
            if (!ts_parser_set_language(parser, grammar))
                return null;

            var tree = ts_parser_parse_string_encoding(
                parser, IntPtr.Zero, utf8, (uint)utf8.Length, TSInputEncodingUTF8);
            if (tree == IntPtr.Zero)
                return null;

            try
            {
                var root = ts_tree_root_node(tree);

                var splittable = new List<TSNode>();
                Collect(root, splittable, splittableKinds, chunkSize);
                if (splittable.Count == 0)
                    return null;

                // Build a lookup from UTF-8 byte index to .NET char index. Most
                // source files are ASCII so this table is mostly identity, but
                // it correctly handles multi-byte characters (e.g. UTF-8 BOMs,
                // unicode identifiers, comments in non-ASCII).
                int[] byteToChar = BuildByteToCharMap(source, utf8.Length);

                var chunks = new List<Chunk>();
                int chunkStartByte = -1;
                int chunkEndByte = -1;
                foreach (var node in splittable)
                {
                    int s = (int)ts_node_start_byte(node);
                    int e = (int)ts_node_end_byte(node);
                    if (e <= s)
                        continue;

                    if (chunkStartByte < 0)
                    {
                        chunkStartByte = s;
                        chunkEndByte = e;
                    }
                    else if (e - chunkStartByte <= chunkSize)
                    {
                        chunkEndByte = e;
                    }
                    else
                    {
                        chunks.Add(MakeChunk(source, filePath, language,
                            chunkStartByte, chunkEndByte, byteToChar));
                        chunkStartByte = s;
                        chunkEndByte = e;
                    }
                }
                if (chunkStartByte >= 0)
                {
                    chunks.Add(MakeChunk(source, filePath, language,
                        chunkStartByte, chunkEndByte, byteToChar));
                }

                return chunks.Count == 0 ? null : chunks;
            }
            finally
            {
                ts_tree_delete(tree);
            }
        }
        finally
        {
            ts_parser_delete(parser);
        }
    }

    private static void Collect(
        TSNode node,
        List<TSNode> result,
        IReadOnlySet<string> splittableKinds,
        int chunkSize)
    {
        uint count = ts_node_child_count(node);
        for (uint i = 0; i < count; i++)
        {
            var child = ts_node_child(node, i);
            if (!ts_node_is_named(child))
                continue;
            var kind = NodeKind(child);
            if (splittableKinds.Contains(kind))
            {
                int size = (int)(ts_node_end_byte(child) - ts_node_start_byte(child));
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

    private static Chunk MakeChunk(
        string source,
        string filePath,
        string? language,
        int startByte,
        int endByte,
        int[] byteToChar)
    {
        int startCh = byteToChar[startByte];
        int endCh = byteToChar[endByte];
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

    /// <summary>
    /// For each UTF-8 byte index 0..byteCount, return the corresponding .NET
    /// (UTF-16 char) index. The returned array has length byteCount + 1 so
    /// end-exclusive byte offsets can also be looked up.
    /// </summary>
    private static int[] BuildByteToCharMap(string source, int byteCount)
    {
        var map = new int[byteCount + 1];
        var utf8 = Encoding.UTF8;
        int charIndex = 0;
        int byteIndex = 0;
        // Walk character by character, advancing byteIndex by the UTF-8 length
        // of each .NET char (or surrogate pair). Fast path: bulk-scan ASCII.
        var span = source.AsSpan();
        var buf = new byte[4];
        while (charIndex < span.Length)
        {
            char c = span[charIndex];
            if (c < 0x80)
            {
                map[byteIndex] = charIndex;
                byteIndex++;
                charIndex++;
            }
            else
            {
                int charsConsumed = char.IsHighSurrogate(c) && charIndex + 1 < span.Length
                    && char.IsLowSurrogate(span[charIndex + 1]) ? 2 : 1;
                int bytesWritten = utf8.GetBytes(
                    span.Slice(charIndex, charsConsumed), buf);
                for (int b = 0; b < bytesWritten; b++)
                    map[byteIndex + b] = charIndex;
                byteIndex += bytesWritten;
                charIndex += charsConsumed;
            }
        }
        // Sentinel: byteCount maps to source.Length so end-exclusive lookups
        // round to the end of the .NET string.
        map[byteCount] = source.Length;
        return map;
    }
}

/// <summary>
/// Per-language tree-sitter grammar bindings. Each grammar lives in its own
/// `libtree-sitter-<lang>` native library built from vendored source under
/// `dotnet/native/<grammar>` and resolved at runtime via P/Invoke.
/// </summary>
public static class TreeSitterGrammars
{
    public static IntPtr Cpp => TreeSitterCppNative.tree_sitter_cpp();

    /// <summary>
    /// Whitelist of node kinds the cpp chunker treats as splittable units.
    /// Tree-sitter-cpp emits these names; non-named anonymous nodes (`;`, `{`)
    /// are filtered out via <see cref="TreeSitterInterop.ts_node_is_named"/>.
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
}
