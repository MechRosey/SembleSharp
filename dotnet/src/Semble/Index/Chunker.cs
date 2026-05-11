using System.Text;

namespace Semble.Index;

/// <summary>
/// Mirrors src/semble/index/chunker.py. The Chonkie/tree-sitter code-aware
/// chunker is deferred; this port always uses line-based chunking, which the
/// upstream code also falls back to when Chonkie fails or returns nothing.
/// </summary>
public static class Chunker
{
    public const int DefaultMaxLines = 50;
    public const int DefaultOverlapLines = 5;

    private static readonly Encoding LenientUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>
    /// Chunk a single file from disk. Returns [] on any read error.
    /// </summary>
    /// <remarks>
    /// For binary document formats (PDF, .docx, .xlsx, .pptx) the registered
    /// <see cref="ITextExtractor"/> in <see cref="TextExtractors.Current"/>
    /// is invoked to produce text plus a language hint; the returned text
    /// then flows through <see cref="ChunkSource"/> like any other file.
    /// When no extractor is registered or available for the extension we
    /// fall back to <see cref="File.ReadAllText(string)"/>.
    /// </remarks>
    public static List<Chunk> ChunkFile(string filePath)
    {
        if (new FileInfo(filePath).Length > 1_000_000)
            return new List<Chunk>();

        var extractor = TextExtractors.Current.For(filePath);
        try
        {
            if (extractor is not null)
            {
                var extracted = extractor.ExtractText(filePath);
                // PDF-style extractors surface page breaks; route through the
                // page-aware chunker so each chunk gets a Locator.Pages.
                if (extracted.PageBreaks is { Count: > 0 })
                {
                    var pdfChunks = PdfChunker.TryChunk(extracted.Text, extracted.PageBreaks, filePath);
                    if (pdfChunks is { Count: > 0 })
                        return pdfChunks;
                }
                return ChunkSource(extracted.Text, filePath, extracted.Language);
            }
            var source = File.ReadAllText(filePath, LenientUtf8);
            return ChunkSource(source, filePath, FileWalker.LanguageForPath(filePath));
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidOperationException
                                   or TimeoutException)
        {
            // Treat extractor failures the same as a plain-read error: skip
            // the file rather than aborting the whole walk.
            return new List<Chunk>();
        }
    }

    /// <summary>Chunk pre-read source text. Whitespace-only input returns [].</summary>
    public static List<Chunk> ChunkSource(string source, string filePath, string? language)
    {
        if (source.AsSpan().Trim().Length == 0)
            return new List<Chunk>();

        // Language-specific code-aware chunkers; each returns null on no usable
        // result, in which case we fall back to line-based chunking.
        var aware = TryCodeAwareChunk(source, filePath, language);
        if (aware is { Count: > 0 })
            return aware;

        return ChunkLines(source, filePath, language);
    }

    private static List<Chunk>? TryCodeAwareChunk(string source, string filePath, string? language) =>
        language switch
        {
            "csharp" => RoslynChunker.TryChunkCSharp(source, filePath, language),
            "cpp" => TreeSitterChunker.TryChunk(
                source, filePath, language,
                TreeSitterGrammars.Cpp,
                TreeSitterGrammars.CppSplittableKinds),
            "markdown" => MarkdownChunker.TryChunk(source, filePath, language),
            _ => null,
        };

    /// <summary>Split <paramref name="source"/> by line count with overlap.</summary>
    public static List<Chunk> ChunkLines(
        string source,
        string filePath,
        string? language = null,
        int maxLines = DefaultMaxLines,
        int overlapLines = DefaultOverlapLines)
    {
        var lines = SplitLinesKeepEnds(source);
        if (lines.Count == 0)
            return new List<Chunk>();

        var chunks = new List<Chunk>();
        int start = 0;
        while (start < lines.Count)
        {
            int end = Math.Min(start + maxLines, lines.Count);
            var sb = new StringBuilder();
            for (int i = start; i < end; i++)
                sb.Append(lines[i]);
            var content = sb.ToString();
            if (content.AsSpan().Trim().Length > 0)
            {
                chunks.Add(new Chunk(
                    Content: content,
                    FilePath: filePath,
                    StartLine: start + 1,
                    EndLine: end,
                    Language: language));
            }
            start = end < lines.Count ? end - overlapLines : end;
        }
        return chunks;
    }

    /// <summary>
    /// Mimic Python's str.splitlines(keepends=True) for the line terminators we
    /// care about (\n, \r\n, \r). The returned segments include their trailing
    /// terminator; the final segment does not if the source has no trailing one.
    /// </summary>
    private static List<string> SplitLinesKeepEnds(string source)
    {
        var result = new List<string>();
        if (source.Length == 0)
            return result;

        int start = 0;
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            if (c == '\n')
            {
                result.Add(source[start..(i + 1)]);
                i++;
                start = i;
            }
            else if (c == '\r')
            {
                int end = (i + 1 < source.Length && source[i + 1] == '\n') ? i + 2 : i + 1;
                result.Add(source[start..end]);
                i = end;
                start = i;
            }
            else
            {
                i++;
            }
        }
        if (start < source.Length)
            result.Add(source[start..]);
        return result;
    }
}
