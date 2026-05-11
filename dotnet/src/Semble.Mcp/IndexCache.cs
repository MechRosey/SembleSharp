namespace Semble.Mcp;

/// <summary>
/// Cache of indexed repos and local paths for the lifetime of the MCP server.
/// Mirrors the Python `_IndexCache` in src/semble/mcp.py:
///  - first call per (source, ref) pair builds the index off the calling thread
///  - subsequent calls share the same Task (and thus the same SembleIndex)
///  - failed builds are evicted so the next caller can retry
///  - at most 10 entries are kept; the least recently used entry is evicted first
/// </summary>
public sealed class IndexCache
{
    private const int MaxCacheSize = 10;

    private readonly IEncoder _model;
    private readonly object _lock = new();
    private readonly Dictionary<string, Task<SembleIndex>> _tasks = new();
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new();

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

        lock (_lock)
        {
            if (_tasks.TryGetValue(cacheKey, out var existing))
            {
                _lru.Remove(_lruNodes[cacheKey]);
                _lruNodes[cacheKey] = _lru.AddFirst(cacheKey);
                return existing;
            }

            if (_tasks.Count >= MaxCacheSize)
            {
                var lruKey = _lru.Last!.Value;
                _lru.RemoveLast();
                _lruNodes.Remove(lruKey);
                _tasks.Remove(lruKey);
            }

            Task<SembleIndex> built = isGit
                ? Task.Run(() => FromGit(source, @ref, _model))
                : Task.Run(() => FromPath(cacheKey, _model));

            built.ContinueWith(
                t =>
                {
                    if (t.IsFaulted || t.IsCanceled)
                    {
                        lock (_lock)
                        {
                            if (_tasks.TryGetValue(cacheKey, out var stored) && ReferenceEquals(stored, t))
                            {
                                _tasks.Remove(cacheKey);
                                if (_lruNodes.Remove(cacheKey, out var node))
                                    _lru.Remove(node);
                            }
                        }
                    }
                },
                TaskContinuationOptions.ExecuteSynchronously);

            _tasks[cacheKey] = built;
            _lruNodes[cacheKey] = _lru.AddFirst(cacheKey);
            return built;
        }
    }

    /// <summary>Remove a cached entry so the next <see cref="GetAsync"/> call rebuilds it.</summary>
    public void Invalidate(string source)
    {
        bool isGit = Formatting.IsGitUrl(source);
        string cacheKey = isGit ? source : System.IO.Path.GetFullPath(source);
        lock (_lock)
        {
            if (_tasks.Remove(cacheKey) && _lruNodes.Remove(cacheKey, out var node))
                _lru.Remove(node);
        }
    }
}
