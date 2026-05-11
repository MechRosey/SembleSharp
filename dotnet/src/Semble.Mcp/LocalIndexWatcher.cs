namespace Semble.Mcp;

/// <summary>
/// Watches a local directory for file-system changes and invalidates the cached
/// index after a debounce period, triggering a background rebuild.
/// </summary>
public sealed class LocalIndexWatcher : IDisposable
{
    private readonly IndexCache _cache;
    private readonly string _path;
    private readonly int _debounceMs;
    private readonly FileSystemWatcher _watcher;
    private readonly object _timerLock = new();
    private Timer? _debounce;

    public LocalIndexWatcher(IndexCache cache, string path, int debounceMs = 1600)
    {
        _cache = cache;
        _path = System.IO.Path.GetFullPath(path);
        _debounceMs = debounceMs;

        _watcher = new FileSystemWatcher(_path)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
        };
        _watcher.Changed += OnChange;
        _watcher.Created += OnChange;
        _watcher.Deleted += OnChange;
        _watcher.Renamed += OnChange;
    }

    private void OnChange(object _, FileSystemEventArgs __)
    {
        lock (_timerLock)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ =>
            {
                _cache.Invalidate(_path);
                _ = _cache.GetAsync(_path);
            }, null, _debounceMs, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        lock (_timerLock) { _debounce?.Dispose(); }
    }
}
