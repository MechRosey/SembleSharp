using System.Collections.Concurrent;

namespace Semble.Mcp;

/// <summary>
/// Cache of indexed repos and local paths for the lifetime of the MCP server.
/// Mirrors the Python `_IndexCache` in src/semble/mcp.py:
///  - first call per (source, ref) pair builds the index off the calling thread
///  - subsequent calls share the same Task (and thus the same SembleIndex)
///  - failed builds are evicted so the next caller can retry
/// </summary>
public sealed class IndexCache
{
    private readonly IEncoder _model;
    private readonly ConcurrentDictionary<string, Task<SembleIndex>> _tasks = new();

    /// <summary>Test seam: defaults to <see cref="SembleIndex.FromPath"/>.</summary>
    public Func<string, IEncoder, SembleIndex> FromPath { get; init; } =
        (path, model) => SembleIndex.FromPath(path, model);

    /// <summary>Test seam: defaults to <see cref="SembleIndex.FromGit"/>.</summary>
    public Func<string, string?, IEncoder, SembleIndex> FromGit { get; init; } =
        (url, @ref, model) => SembleIndex.FromGit(url, @ref, model);

    public IndexCache(IEncoder model)
    {
        _model = model;
    }

    public Task<SembleIndex> GetAsync(string source, string? @ref = null)
    {
        bool isGit = Formatting.IsGitUrl(source);
        string cacheKey = isGit
            ? (@ref is not null ? $"{source}@{@ref}" : source)
            : System.IO.Path.GetFullPath(source);

        var task = _tasks.GetOrAdd(cacheKey, _ =>
        {
            Task<SembleIndex> built = isGit
                ? Task.Run(() => FromGit(source, @ref, _model))
                : Task.Run(() => FromPath(cacheKey, _model));

            // Evict on failure so the next caller can retry.
            built.ContinueWith(
                t =>
                {
                    if (t.IsFaulted || t.IsCanceled)
                        _tasks.TryRemove(new KeyValuePair<string, Task<SembleIndex>>(cacheKey, t));
                },
                TaskContinuationOptions.ExecuteSynchronously);

            return built;
        });

        return task;
    }
}
