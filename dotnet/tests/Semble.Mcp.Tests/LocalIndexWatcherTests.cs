using Semble;
using Semble.Index;
using Semble.Mcp;
using static Semble.Tests.Fixtures;

namespace Semble.Mcp.Tests;

public class LocalIndexWatcherTests : IDisposable
{
    private readonly string _tmp;

    public LocalIndexWatcherTests()
    {
        _tmp = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "semble-watcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Watcher_Triggers_Reindex_After_Debounce_When_File_Changes()
    {
        var index = FakeIndex.Build(new[] { MakeChunk("x = 1", "src/foo.py") });
        int callCount = 0;
        var cache = new IndexCache(new MockEncoder())
        {
            FromPath = (_, _) => { callCount++; return index; },
        };

        await cache.GetAsync(_tmp);
        Assert.Equal(1, callCount);

        using var watcher = new LocalIndexWatcher(cache, _tmp, debounceMs: 50);

        File.WriteAllText(System.IO.Path.Combine(_tmp, "new_file.py"), "y = 2\n");

        await Task.Delay(300);

        Assert.Equal(2, callCount);
    }
}
