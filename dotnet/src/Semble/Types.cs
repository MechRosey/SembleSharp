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
/// A single indexable unit of code.
/// </summary>
public sealed record Chunk(
    string Content,
    string FilePath,
    int StartLine,
    int EndLine,
    string? Language = null)
{
    /// <summary>File path and line range as "file_path:start_line-end_line".</summary>
    public string Location => $"{FilePath}:{StartLine}-{EndLine}";
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
