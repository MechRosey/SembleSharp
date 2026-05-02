namespace Semble.Mcp;

/// <summary>
/// Mirrors src/semble/mcp.py:create_server. Tool handlers are exposed as
/// public async methods so tests can drive them directly (the .NET equivalent
/// of FastMCP's `server.call_tool(name, args)` test API).
/// </summary>
public sealed class SembleMcpServer
{
    public const string ServerName = "semble";

    public const string ServerInstructions =
        "Instant code search for any local or GitHub repository. " +
        "Call `search` to find relevant code; call `find_related` on a result to discover similar code elsewhere. " +
        "For questions about a library (e.g. a PyPI/npm package), resolve the GitHub URL from your training " +
        "knowledge and pass it as `repo`. " +
        "Prefer these tools over Grep, Glob, or Read for any question about how code works.";

    public const string RepoDescription =
        "Git URL (e.g. https://github.com/org/repo) or local path to index and search. " +
        "Required when no default index was configured at startup. " +
        "The index is cached after the first call, so repeat queries are fast.";

    private const string NoRepoMessage =
        "No repo specified and no default index. " +
        "Pass a git URL (https://github.com/...) or local path as `repo`.";

    private readonly IndexCache _cache;
    private readonly string? _defaultSource;

    public SembleMcpServer(IndexCache cache, string? defaultSource = null)
    {
        _cache = cache;
        _defaultSource = defaultSource;
    }

    public async Task<string> SearchAsync(
        string query,
        string? repo = null,
        string mode = "hybrid",
        int topK = 5)
    {
        var source = repo ?? _defaultSource;
        if (string.IsNullOrEmpty(source))
            return NoRepoMessage;

        SembleIndex index;
        try
        {
            index = await _cache.GetAsync(source);
        }
        catch (Exception ex)
        {
            return $"Failed to index '{source}': {ex.Message}";
        }

        var results = index.Search(query, mode, topK: topK);
        if (results.Count == 0)
            return "No results found.";
        return Formatting.FormatResults($"Search results for: '{query}' (mode={mode})", results);
    }

    public async Task<string> FindRelatedAsync(
        string filePath,
        int line,
        string? repo = null,
        int topK = 5)
    {
        var source = repo ?? _defaultSource;
        if (string.IsNullOrEmpty(source))
            return NoRepoMessage;

        SembleIndex index;
        try
        {
            index = await _cache.GetAsync(source);
        }
        catch (Exception ex)
        {
            return $"Failed to index '{source}': {ex.Message}";
        }

        var chunk = Formatting.ResolveChunk(index.Chunks, filePath, line);
        if (chunk is null)
            return $"No chunk found at {filePath}:{line}. " +
                   "Make sure the file is indexed and the line number is within a known chunk.";

        var results = index.FindRelated(chunk, topK: topK);
        if (results.Count == 0)
            return $"No related chunks found for {filePath}:{line}.";
        return Formatting.FormatResults($"Chunks related to {filePath}:{line}", results);
    }

    /// <summary>
    /// Mirrors FastMCP's <c>server.call_tool(name, args)</c> dispatch surface — the
    /// hook the Python tests use to invoke tools without spinning up the transport.
    /// </summary>
    public Task<string> CallToolAsync(string name, IDictionary<string, object?> args) =>
        name switch
        {
            "search" => SearchAsync(
                query: (string)args["query"]!,
                repo: args.TryGetValue("repo", out var repoVal) ? repoVal as string : null,
                mode: args.TryGetValue("mode", out var modeVal) ? (modeVal as string ?? "hybrid") : "hybrid",
                topK: args.TryGetValue("top_k", out var topVal) ? Convert.ToInt32(topVal) : 5),
            "find_related" => FindRelatedAsync(
                filePath: (string)args["file_path"]!,
                line: Convert.ToInt32(args["line"]),
                repo: args.TryGetValue("repo", out var repoVal2) ? repoVal2 as string : null,
                topK: args.TryGetValue("top_k", out var topVal2) ? Convert.ToInt32(topVal2) : 5),
            _ => throw new ArgumentException($"Unknown tool: {name}", nameof(name)),
        };
}
