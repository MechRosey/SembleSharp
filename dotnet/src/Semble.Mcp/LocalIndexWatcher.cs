namespace Semble.Mcp;

/// <summary>
/// Watches a local directory for changes and debounces rebuilds via IndexCache.Invalidate.
/// </summary>
public sealed class LocalIndexWatcher : IDisposable
{
    public LocalIndexWatcher(IndexCache cache, string path, int debounceMs = 1600)
    {
        throw new NotImplementedException();
    }

    public void Dispose() { }
}
