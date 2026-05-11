using System.Text;

namespace Semble.Index;

/// <summary>
/// Page-aware chunker for extracted PDF text. Each emitted chunk is bounded
/// to a single page, carries the page number in <see cref="Locator.Pages"/>,
/// and is sub-windowed with the same 50-line / 5-line-overlap shape as
/// <see cref="Chunker.ChunkLines"/> when a page is too long for one chunk.
/// </summary>
/// <remarks>
/// Driven by an <see cref="ExtractedText.PageBreaks"/> list of character
/// offsets — typically populated by <see cref="Extractors.PdftotextExtractor"/>
/// from form-feed (\f) page separators. Markdown-producing extractors don't
/// populate page breaks; their output flows through
/// <see cref="MarkdownChunker"/> instead.
/// </remarks>
public static class PdfChunker
{
    public const int DefaultMaxLines = 50;
    public const int DefaultOverlapLines = 5;

    /// <summary>
    /// Split <paramref name="text"/> into per-page chunks. Returns null when
    /// the page-break list is empty (caller falls back to line-based chunking).
    /// </summary>
    public static List<Chunk>? TryChunk(
        string text,
        IReadOnlyList<int> pageBreaks,
        string filePath,
        string? language = "pdf",
        int maxLines = DefaultMaxLines,
        int overlapLines = DefaultOverlapLines)
    {
        if (pageBreaks.Count == 0 || text.Length == 0)
            return null;

        // Index every line's [start, end) char range so we can map char
        // offsets to 0-indexed line numbers in O(log n).
        var lineStarts = BuildLineStarts(text);
        if (lineStarts.Count == 0)
            return null;
        var lines = SplitLinesKeepingNewline(text);

        var chunks = new List<Chunk>();
        for (int p = 0; p < pageBreaks.Count; p++)
        {
            int charStart = pageBreaks[p];
            int charEnd = (p + 1 < pageBreaks.Count) ? pageBreaks[p + 1] : text.Length;
            int pageNumber = p + 1;

            // Step back over the page-separator form feed(s) at the end of
            // this page so the \f doesn't bleed into chunk content or extend
            // the page's line range across the boundary.
            while (charEnd > charStart && text[charEnd - 1] == '\f')
                charEnd--;

            if (charEnd <= charStart)
                continue;

            int firstLine = LineIndexForChar(lineStarts, charStart);
            int lastLine = LineIndexForChar(lineStarts, charEnd - 1);
            if (lastLine < firstLine)
                continue;

            // Skip pages that are nothing but whitespace + form-feeds.
            bool anyContent = false;
            for (int i = firstLine; i <= lastLine; i++)
            {
                if (!IsWhitespaceOnly(lines[i]))
                {
                    anyContent = true;
                    break;
                }
            }
            if (!anyContent)
                continue;

            // Window within the page's line range, never crossing into the
            // next page even if maxLines would allow it.
            int s = firstLine;
            while (s <= lastLine)
            {
                int e = Math.Min(s + maxLines - 1, lastLine);

                var sb = new StringBuilder();
                for (int i = s; i <= e; i++)
                    sb.Append(lines[i]);
                var content = sb.ToString();

                if (!string.IsNullOrWhiteSpace(content))
                {
                    chunks.Add(new Chunk(
                        Content: content,
                        FilePath: filePath,
                        StartLine: s + 1,         // 1-indexed
                        EndLine: e + 1,
                        Language: language,
                        Locator: new Locator.Pages(pageNumber, pageNumber)));
                }

                if (e >= lastLine)
                    break;
                int next = e - overlapLines + 1;
                if (next <= s)
                    next = s + 1; // guarantee progress when overlap >= maxLines
                s = next;
            }
        }

        return chunks.Count == 0 ? null : chunks;
    }

    /// <summary>
    /// Compute every line's starting char offset. <c>\f</c> (form feed) is
    /// treated as a line terminator alongside <c>\n</c> / <c>\r</c> so a
    /// page-separator <c>\f</c> never shares a line with text on either side
    /// of the page boundary.
    /// </summary>
    private static List<int> BuildLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n' || c == '\f')
            {
                if (i + 1 < text.Length)
                    starts.Add(i + 1);
            }
            else if (c == '\r')
            {
                int newlineEnd = (i + 1 < text.Length && text[i + 1] == '\n') ? i + 2 : i + 1;
                if (newlineEnd < text.Length)
                    starts.Add(newlineEnd);
                i = newlineEnd - 1;
            }
        }
        return starts;
    }

    private static List<string> SplitLinesKeepingNewline(string text)
    {
        var result = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n' || c == '\f')
            {
                result.Add(text[start..(i + 1)]);
                start = i + 1;
            }
            else if (c == '\r')
            {
                int end = (i + 1 < text.Length && text[i + 1] == '\n') ? i + 2 : i + 1;
                result.Add(text[start..end]);
                i = end - 1;
                start = end;
            }
        }
        if (start < text.Length)
            result.Add(text[start..]);
        return result;
    }

    private static int LineIndexForChar(List<int> lineStarts, int charOffset)
    {
        if (charOffset < 0)
            return 0;
        // binary search for largest i such that lineStarts[i] <= charOffset
        int lo = 0, hi = lineStarts.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (lineStarts[mid] <= charOffset)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo;
    }

    private static bool IsWhitespaceOnly(string s)
    {
        foreach (var c in s)
        {
            if (!char.IsWhiteSpace(c) && c != '\f')
                return false;
        }
        return true;
    }
}
