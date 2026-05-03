namespace Semble;

/// <summary>
/// Search mode for <see cref="SembleIndex"/>.Search.
/// </summary>
public enum SearchMode
{
    Hybrid,
    Semantic,
    Bm25,
}

/// <summary>
/// String<->enum conversions for <see cref="SearchMode"/> matching the Python wire values
/// ("hybrid", "semantic", "bm25"). The CLI and MCP tool inputs are these literal strings.
/// </summary>
public static class SearchModeExtensions
{
    public static string ToWire(this SearchMode mode) => mode switch
    {
        SearchMode.Hybrid => "hybrid",
        SearchMode.Semantic => "semantic",
        SearchMode.Bm25 => "bm25",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    public static SearchMode ParseMode(string value) => value switch
    {
        "hybrid" => SearchMode.Hybrid,
        "semantic" => SearchMode.Semantic,
        "bm25" => SearchMode.Bm25,
        _ => throw new ArgumentException($"Unknown search mode '{value}'", nameof(value)),
    };
}

/// <summary>
/// Protocol for embedding models. Returns a 2D float32 matrix of shape
/// (texts.Count, embedding_dim).
/// </summary>
public interface IEncoder
{
    float[,] Encode(IReadOnlyList<string> texts);
}

/// <summary>
/// A single indexable unit of code or document text.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StartLine"/> / <see cref="EndLine"/> are always populated and
/// always refer to lines of <see cref="Content"/>. For source files extracted
/// directly from disk these match the file's own line numbering. For
/// content extracted from binary formats (PDF, Office, etc.) they refer to
/// lines of the extracted text — the document's own coordinates (page,
/// slide, sheet, heading path) live in the optional <see cref="Locator"/>.
/// </para>
/// <para>
/// <see cref="Location"/> renders the canonical short form used in CLI / MCP
/// search output. When <see cref="Locator"/> is null it's
/// <c>file_path:start_line-end_line</c> — backwards-compatible with the
/// upstream Python format.
/// </para>
/// </remarks>
public sealed record Chunk(
    string Content,
    string FilePath,
    int StartLine,
    int EndLine,
    string? Language = null,
    Locator? Locator = null)
{
    /// <summary>Canonical short form used in CLI / MCP output.</summary>
    public string Location => Locator is { } loc
        ? $"{FilePath}:{loc.Render(StartLine, EndLine)}"
        : $"{FilePath}:{StartLine}-{EndLine}";
}

/// <summary>
/// Document-format-specific coordinate that augments or replaces the
/// generic line range on a <see cref="Chunk"/>. Each variant renders a
/// short suffix that <see cref="Chunk.Location"/> appends after the file
/// path; line ranges are passed in so variants that want to keep them can
/// (e.g. <see cref="Heading"/>) and variants for which lines are
/// meaningless (e.g. <see cref="Pages"/>) can drop them.
/// </summary>
public abstract record Locator
{
    /// <summary>Render the locator as the suffix after <c>file_path:</c>.</summary>
    public abstract string Render(int startLine, int endLine);

    /// <summary>PDF page or page-range coordinate.</summary>
    public sealed record Pages(int Start, int End) : Locator
    {
        public override string Render(int startLine, int endLine) =>
            Start == End ? $"p{Start}" : $"p{Start}-{End}";
    }

    /// <summary>PowerPoint slide index (1-indexed).</summary>
    public sealed record Slide(int Index) : Locator
    {
        public override string Render(int startLine, int endLine) => $"slide{Index}";
    }

    /// <summary>Excel sheet name + cell range (e.g. <c>Sheet1!A1:C10</c>).</summary>
    public sealed record Sheet(string Name, string CellRange) : Locator
    {
        public override string Render(int startLine, int endLine) => $"{Name}!{CellRange}";
    }

    /// <summary>
    /// Heading-path coordinate for markdown / Word documents (e.g.
    /// <c>["Architecture", "Storage"]</c>). Renders as a slash-joined path
    /// followed by the line range so the reader can locate the chunk both
    /// semantically and lexically. An empty path renders as just the line range.
    /// </summary>
    public sealed record Heading(IReadOnlyList<string> Path) : Locator
    {
        public override string Render(int startLine, int endLine) =>
            Path.Count == 0
                ? $"{startLine}-{endLine}"
                : $"{string.Join("/", Path)}:{startLine}-{endLine}";

        public bool Equals(Heading? other) =>
            other is not null && Path.SequenceEqual(other.Path);

        public override int GetHashCode()
        {
            var hc = new HashCode();
            foreach (var p in Path) hc.Add(p);
            return hc.ToHashCode();
        }
    }
}

/// <summary>
/// A single search result with score and source retriever. Score is double-precision
/// to match Python's float64 — matters for hybrid/RRF arithmetic.
/// </summary>
public sealed record SearchResult(Chunk Chunk, double Score, SearchMode Source);

/// <summary>
/// Statistics about the current index state.
/// </summary>
public sealed record IndexStats(
    int IndexedFiles = 0,
    int TotalChunks = 0,
    IReadOnlyDictionary<string, int>? Languages = null)
{
    public IReadOnlyDictionary<string, int> Languages { get; init; } =
        Languages ?? new Dictionary<string, int>();
}
