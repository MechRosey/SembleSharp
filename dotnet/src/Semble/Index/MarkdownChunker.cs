using System.Text;

namespace Semble.Index;

/// <summary>
/// Markdown-aware chunker: splits on heading boundaries (deepest level first
/// when a section exceeds the budget), then paragraphs (blank-line separated),
/// then falls back to line-based for runaway code fences or wall-of-text.
/// Each emitted chunk records its enclosing heading path in
/// <see cref="Locator.Heading"/> so search results can show breadcrumbs.
/// </summary>
public static class MarkdownChunker
{
    public const int DefaultChunkSize = 1500;

    /// <summary>
    /// Chunk markdown source. Returns null when the input is empty or contains
    /// no useful text so the caller can fall back to the line-based chunker.
    /// </summary>
    public static List<Chunk>? TryChunk(
        string source,
        string filePath,
        string? language,
        int chunkSize = DefaultChunkSize)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;

        var lines = SplitLines(source);
        var sections = BuildSections(lines);
        if (sections.Count == 0)
            return null;

        // Greedy-merge consecutive sections sharing the same heading path,
        // splitting any oversized section first into paragraphs and then into
        // line windows. Each emitted chunk is bounded above by chunkSize.
        var chunks = new List<Chunk>();
        Section? merged = null;
        foreach (var sec in sections)
        {
            int secSize = sec.CharCount;
            if (secSize > chunkSize)
            {
                // Flush whatever we've been accumulating.
                if (merged is not null)
                {
                    chunks.Add(MakeChunk(source, filePath, language, lines, merged));
                    merged = null;
                }
                // Split the oversized section.
                foreach (var sub in SplitOversizedSection(lines, sec, chunkSize))
                    chunks.Add(MakeChunk(source, filePath, language, lines, sub));
                continue;
            }

            if (merged is null)
            {
                merged = sec;
            }
            else if (HeadingPathEquals(merged.HeadingPath, sec.HeadingPath)
                     && merged.CharCount + secSize <= chunkSize
                     && sec.StartLine == merged.EndLine + 1)
            {
                merged = merged with
                {
                    EndLine = sec.EndLine,
                    CharCount = merged.CharCount + secSize,
                };
            }
            else
            {
                chunks.Add(MakeChunk(source, filePath, language, lines, merged));
                merged = sec;
            }
        }
        if (merged is not null)
            chunks.Add(MakeChunk(source, filePath, language, lines, merged));

        return chunks.Count == 0 ? null : chunks;
    }

    private sealed record Section(IReadOnlyList<string> HeadingPath, int StartLine, int EndLine, int CharCount);

    private static List<Section> BuildSections(IReadOnlyList<string> lines)
    {
        var sections = new List<Section>();
        var headingStack = new List<(int Level, string Title)>();

        int sectionStart = 1;
        var currentPath = ImmutableHeadingPath(headingStack);
        int sectionChars = 0;
        bool inFencedCode = false;
        string? fenceMarker = null;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            // Track fenced code blocks: never treat ``` lines or anything
            // inside a code fence as headings.
            if (TryGetFenceMarker(line, out var marker))
            {
                if (!inFencedCode)
                {
                    inFencedCode = true;
                    fenceMarker = marker;
                }
                else if (marker == fenceMarker)
                {
                    inFencedCode = false;
                    fenceMarker = null;
                }
                sectionChars += line.Length + 1;
                continue;
            }

            if (!inFencedCode && TryParseAtxHeading(line, out int level, out string title))
            {
                int currentLine = i + 1; // 1-indexed
                if (currentLine - 1 >= sectionStart)
                {
                    var sectionEnd = currentLine - 1;
                    if (sectionChars > 0)
                        sections.Add(new Section(currentPath, sectionStart, sectionEnd, sectionChars));
                }

                // Pop stack to one above this heading's level, then push.
                while (headingStack.Count > 0 && headingStack[^1].Level >= level)
                    headingStack.RemoveAt(headingStack.Count - 1);
                headingStack.Add((level, title));
                currentPath = ImmutableHeadingPath(headingStack);

                sectionStart = currentLine;
                sectionChars = line.Length + 1;
                continue;
            }

            sectionChars += line.Length + 1;
        }

        if (sectionChars > 0 && sectionStart <= lines.Count)
            sections.Add(new Section(currentPath, sectionStart, lines.Count, sectionChars));

        return sections;
    }

    private static IEnumerable<Section> SplitOversizedSection(IReadOnlyList<string> lines, Section section, int chunkSize)
    {
        // Walk paragraphs (blank-line separated). Greedy-merge until the budget.
        var paragraphs = SplitParagraphs(lines, section.StartLine, section.EndLine);
        Section? running = null;
        foreach (var para in paragraphs)
        {
            int paraChars = ParaCharCount(lines, para.StartLine, para.EndLine);

            if (paraChars > chunkSize)
            {
                // Flush running.
                if (running is not null)
                {
                    yield return running;
                    running = null;
                }
                // Fall back to line windows for runaway paragraphs.
                foreach (var win in SplitByLineWindow(section.HeadingPath, lines, para.StartLine, para.EndLine, chunkSize))
                    yield return win;
                continue;
            }

            if (running is null)
            {
                running = new Section(section.HeadingPath, para.StartLine, para.EndLine, paraChars);
            }
            else if (running.CharCount + paraChars <= chunkSize)
            {
                running = running with
                {
                    EndLine = para.EndLine,
                    CharCount = running.CharCount + paraChars,
                };
            }
            else
            {
                yield return running;
                running = new Section(section.HeadingPath, para.StartLine, para.EndLine, paraChars);
            }
        }
        if (running is not null)
            yield return running;
    }

    private static IEnumerable<(int StartLine, int EndLine)> SplitParagraphs(
        IReadOnlyList<string> lines, int startLine, int endLine)
    {
        int? paraStart = null;
        bool inFence = false;
        string? fenceMarker = null;
        for (int n = startLine; n <= endLine; n++)
        {
            var line = lines[n - 1];
            bool fenceLine = TryGetFenceMarker(line, out var marker);
            if (fenceLine)
            {
                if (!inFence) { inFence = true; fenceMarker = marker; }
                else if (marker == fenceMarker) { inFence = false; fenceMarker = null; }
                paraStart ??= n;
                continue;
            }

            // Paragraph break = blank line outside a fence.
            if (!inFence && line.Trim().Length == 0)
            {
                if (paraStart is { } s)
                {
                    yield return (s, n - 1);
                    paraStart = null;
                }
                continue;
            }
            paraStart ??= n;
        }
        if (paraStart is { } last)
            yield return (last, endLine);
    }

    private static int ParaCharCount(IReadOnlyList<string> lines, int startLine, int endLine)
    {
        int n = 0;
        for (int i = startLine; i <= endLine; i++)
            n += lines[i - 1].Length + 1;
        return n;
    }

    private static IEnumerable<Section> SplitByLineWindow(
        IReadOnlyList<string> headingPath,
        IReadOnlyList<string> lines,
        int startLine, int endLine, int chunkSize)
    {
        const int linesPerChunk = 50;
        const int overlap = 5;
        int s = startLine;
        while (s <= endLine)
        {
            int e = Math.Min(s + linesPerChunk - 1, endLine);
            int chars = ParaCharCount(lines, s, e);
            yield return new Section(headingPath, s, e, chars);
            if (e >= endLine)
                break;
            s = e - overlap + 1;
        }
    }

    private static Chunk MakeChunk(
        string source,
        string filePath,
        string? language,
        IReadOnlyList<string> lines,
        Section section)
    {
        var sb = new StringBuilder();
        for (int i = section.StartLine; i <= section.EndLine && i <= lines.Count; i++)
        {
            sb.Append(lines[i - 1]);
            if (i < lines.Count || source.EndsWith('\n'))
                sb.Append('\n');
        }
        return new Chunk(
            Content: sb.ToString(),
            FilePath: filePath,
            StartLine: section.StartLine,
            EndLine: section.EndLine,
            Language: language,
            Locator: new Locator.Heading(section.HeadingPath));
    }

    private static bool TryParseAtxHeading(string line, out int level, out string title)
    {
        level = 0;
        title = string.Empty;
        // Up to three leading spaces are allowed before the # markers (CommonMark).
        int i = 0;
        while (i < line.Length && i < 3 && line[i] == ' ') i++;
        int hashStart = i;
        while (i < line.Length && line[i] == '#') i++;
        int hashCount = i - hashStart;
        if (hashCount < 1 || hashCount > 6)
            return false;
        // ATX heading requires the hashes to be followed by EOL or whitespace.
        if (i < line.Length && line[i] != ' ' && line[i] != '\t')
            return false;
        level = hashCount;
        title = line[i..].Trim().TrimEnd('#').Trim();
        return true;
    }

    private static bool TryGetFenceMarker(string line, out string marker)
    {
        marker = string.Empty;
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            marker = "```";
            return true;
        }
        if (trimmed.StartsWith("~~~", StringComparison.Ordinal))
        {
            marker = "~~~";
            return true;
        }
        return false;
    }

    private static IReadOnlyList<string> ImmutableHeadingPath(IReadOnlyList<(int Level, string Title)> stack)
    {
        if (stack.Count == 0)
            return Array.Empty<string>();
        var arr = new string[stack.Count];
        for (int i = 0; i < stack.Count; i++)
            arr[i] = stack[i].Title;
        return arr;
    }

    private static bool HeadingPathEquals(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }

    private static List<string> SplitLines(string source)
    {
        var result = new List<string>();
        int start = 0;
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '\n')
            {
                result.Add(source[start..i]);
                start = i + 1;
            }
            else if (c == '\r')
            {
                result.Add(source[start..i]);
                if (i + 1 < source.Length && source[i + 1] == '\n')
                    i++;
                start = i + 1;
            }
        }
        if (start < source.Length)
            result.Add(source[start..]);
        return result;
    }
}
