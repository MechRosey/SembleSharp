using Semble;
using Semble.Index;
using Semble.Mcp;
using Xunit;
using static Semble.Tests.Fixtures;

namespace Semble.Mcp.Tests;

internal static class FakeIndex
{
    public static SembleIndex Build(IReadOnlyList<Chunk> chunks)
    {
        var encoder = new MockEncoder();
        var embeddings = encoder.Encode(chunks.Select(c => c.Content).ToList());
        var dense = new DenseBackend(embeddings);
        var bm25 = new Bm25();
        bm25.Index(chunks
            .Select(c => (IReadOnlyList<string>)Tokens.Tokenize(c.Content))
            .ToList());
        return new SembleIndex(encoder, bm25, dense, chunks);
    }
}

public class ResolveChunkTests
{
    [Fact]
    public void Returns_Inner_Chunk_When_Line_Is_Strictly_Inside()
    {
        var interior = MakeChunk("line1\nline2\nline3", "src/a.py");
        Assert.Same(interior, Formatting.ResolveChunk(new[] { interior }, "src/a.py", 2));
    }

    [Fact]
    public void Returns_Boundary_Chunk_When_Line_Equals_End_Line()
    {
        var boundary = MakeChunk("last line", "src/a.py"); // start=1, end=1 (single-line)
        Assert.Same(boundary, Formatting.ResolveChunk(new[] { boundary }, "src/a.py", 1));
    }

    [Fact]
    public void Unknown_File_Returns_Null()
    {
        var interior = MakeChunk("line1\nline2\nline3", "src/a.py");
        Assert.Null(Formatting.ResolveChunk(new[] { interior }, "src/other.py", 1));
    }

    [Fact]
    public void Out_Of_Range_Line_Returns_Null()
    {
        var interior = MakeChunk("line1\nline2\nline3", "src/a.py");
        Assert.Null(Formatting.ResolveChunk(new[] { interior }, "src/a.py", 99));
    }
}

public class GitUrlDetectionTests
{
    [Theory]
    [InlineData("https://github.com/org/repo", true)]
    [InlineData("http://github.com/org/repo", true)]
    [InlineData("git://github.com/org/repo", true)]
    [InlineData("ssh://git@github.com/org/repo", true)]
    [InlineData("git+ssh://git@github.com/org/repo", true)]
    [InlineData("file:///tmp/repo", true)]
    [InlineData("git@github.com:org/repo", true)]    // scp-like
    [InlineData("/local/path/to/repo", false)]
    [InlineData("./relative/path", false)]
    [InlineData("repo_name", false)]
    public void IsGitUrl(string path, bool expected)
    {
        Assert.Equal(expected, Formatting.IsGitUrl(path));
    }
}

public class FormatResultsTests
{
    [Fact]
    public void Empty_List_Yields_Header_With_No_Code_Fences()
    {
        var s = Formatting.FormatResults("My header", Array.Empty<SearchResult>());
        Assert.Contains("My header", s, StringComparison.Ordinal);
        Assert.DoesNotContain("```", s, StringComparison.Ordinal);
    }

    [Fact]
    public void Numbered_Results_Carry_Score_And_Content()
    {
        var chunks = Enumerable.Range(0, 3).Select(i => MakeChunk($"def fn_{i}(): pass", $"f{i}.py")).ToList();
        var results = chunks.Select((c, i) =>
            new SearchResult(c, Math.Round(0.1 * (i + 1), 3), SearchMode.Hybrid)).ToList();
        var s = Formatting.FormatResults("Results for: 'foo'", results);
        Assert.Contains("Results for: 'foo'", s, StringComparison.Ordinal);
        var fenceCount = 0;
        for (int i = 0; i < s.Length - 2; i++)
            if (s[i] == '`' && s[i + 1] == '`' && s[i + 2] == '`') { fenceCount++; i += 2; }
        Assert.True(fenceCount >= results.Count * 2);
        for (int i = 0; i < chunks.Count; i++)
        {
            Assert.Contains($"## {i + 1}.", s, StringComparison.Ordinal);
            Assert.Contains(chunks[i].Content, s, StringComparison.Ordinal);
        }
        Assert.Contains("0.100", s, StringComparison.Ordinal);
        Assert.Contains("0.200", s, StringComparison.Ordinal);
        Assert.Contains("0.300", s, StringComparison.Ordinal);
    }
}

public class IndexCacheTests : IDisposable
{
    private readonly string _tmp;

    public IndexCacheTests()
    {
        _tmp = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "semble-mcp-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Builds_Local_Path_Via_FromPath_And_Caches()
    {
        var index = FakeIndex.Build(new[] { MakeChunk("def foo(): pass", "src/foo.py") });
        int callCount = 0;
        var cache = new IndexCache(new MockEncoder())
        {
            FromPath = (_, _) => { callCount++; return index; },
        };
        var first = await cache.GetAsync(_tmp);
        var second = await cache.GetAsync(_tmp);
        Assert.Same(index, first);
        Assert.Same(index, second);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task Builds_Git_Url_Via_FromGit_And_Caches()
    {
        var index = FakeIndex.Build(new[] { MakeChunk("def foo(): pass", "src/foo.py") });
        int callCount = 0;
        var cache = new IndexCache(new MockEncoder())
        {
            FromGit = (_, _, _) => { callCount++; return index; },
        };
        var first = await cache.GetAsync("https://github.com/org/repo");
        var second = await cache.GetAsync("https://github.com/org/repo");
        Assert.Same(index, first);
        Assert.Same(index, second);
        Assert.Equal(1, callCount);
    }

    private static string[] MakePaths(string prefix, int count)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        return Enumerable.Range(1, count)
            .Select(i => System.IO.Path.Combine(root, "path" + i))
            .ToArray();
    }

    [Fact]
    public async Task Cache_Evicts_Oldest_Entry_When_Full()
    {
        var index = FakeIndex.Build(new[] { MakeChunk("x = 1", "src/foo.py") });
        int callCount = 0;
        var cache = new IndexCache(new MockEncoder())
        {
            FromPath = (_, _) => { callCount++; return index; },
        };

        var paths = MakePaths("semble-lru-", 11);

        for (int i = 0; i < 10; i++)
            await cache.GetAsync(paths[i]);
        Assert.Equal(10, callCount);

        await cache.GetAsync(paths[10]);
        Assert.Equal(11, callCount);

        // paths[0] must have been evicted -- accessing it triggers a rebuild
        await cache.GetAsync(paths[0]);
        Assert.Equal(12, callCount);
    }

    [Fact]
    public async Task Cache_Respects_Lru_Order_On_Eviction()
    {
        var index = FakeIndex.Build(new[] { MakeChunk("x = 1", "src/foo.py") });
        int callCount = 0;
        var cache = new IndexCache(new MockEncoder())
        {
            FromPath = (_, _) => { callCount++; return index; },
        };

        var paths = MakePaths("semble-lru2-", 11);

        for (int i = 0; i < 10; i++)
            await cache.GetAsync(paths[i]);
        Assert.Equal(10, callCount);

        // Re-access paths[0] to promote to MRU; paths[1] becomes the LRU
        await cache.GetAsync(paths[0]);
        Assert.Equal(10, callCount);

        // Add 11th -- should evict paths[1]
        await cache.GetAsync(paths[10]);
        Assert.Equal(11, callCount);

        // paths[1] must be evicted -- accessing it rebuilds
        await cache.GetAsync(paths[1]);
        Assert.Equal(12, callCount);

        // paths[0] must still be cached -- no rebuild
        int before = callCount;
        await cache.GetAsync(paths[0]);
        Assert.Equal(before, callCount);
    }

    [Fact]
    public async Task Failed_Build_Is_Evicted_So_Next_Caller_Can_Retry()
    {
        var index = FakeIndex.Build(new[] { MakeChunk("def foo(): pass", "src/foo.py") });
        int callCount = 0;
        var cache = new IndexCache(new MockEncoder())
        {
            FromPath = (_, _) =>
            {
                callCount++;
                if (callCount == 1) throw new InvalidOperationException("build failed");
                return index;
            },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(_tmp));
        var result = await cache.GetAsync(_tmp);
        Assert.Same(index, result);
        Assert.Equal(2, callCount);
    }
}

public class UnsafeProtocolTests
{
    [Theory]
    [InlineData("ssh://github.com/org/repo")]
    [InlineData("file:///tmp/repo")]
    [InlineData("git+ssh://git@github.com/org/repo")]
    [InlineData("git@github.com:org/repo")]
    public async Task Search_Rejects_Unsafe_Transport(string unsafeRepo)
    {
        int fromGitCallCount = 0;
        var cache = new IndexCache(new MockEncoder())
        {
            FromGit = (_, _, _) => { fromGitCallCount++; throw new InvalidOperationException("should not be called"); },
        };
        var server = new SembleMcpServer(cache);
        var text = await server.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "foo", ["repo"] = unsafeRepo });
        Assert.Contains("not supported", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fromGitCallCount);
    }

    [Theory]
    [InlineData("ssh://github.com/org/repo")]
    [InlineData("file:///tmp/repo")]
    [InlineData("git+ssh://git@github.com/org/repo")]
    [InlineData("git@github.com:org/repo")]
    public async Task FindRelated_Rejects_Unsafe_Transport(string unsafeRepo)
    {
        int fromGitCallCount = 0;
        var cache = new IndexCache(new MockEncoder())
        {
            FromGit = (_, _, _) => { fromGitCallCount++; throw new InvalidOperationException("should not be called"); },
        };
        var server = new SembleMcpServer(cache);
        var text = await server.CallToolAsync("find_related", new Dictionary<string, object?>
        {
            ["file_path"] = "src/foo.py", ["line"] = 1, ["repo"] = unsafeRepo,
        });
        Assert.Contains("not supported", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fromGitCallCount);
    }
}

public class ToolCallTests
{
    private static IndexCache CacheFor(SembleIndex index) =>
        new(new MockEncoder()) { FromPath = (_, _) => index, FromGit = (_, _, _) => index };

    [Fact]
    public async Task Search_With_No_Repo_And_No_Default_Returns_Friendly_Message()
    {
        var cache = new IndexCache(new MockEncoder());
        var server = new SembleMcpServer(cache, defaultSource: null);
        var text = await server.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "foo" });
        Assert.Contains("No repo specified", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindRelated_With_No_Repo_And_No_Default_Returns_Friendly_Message()
    {
        var cache = new IndexCache(new MockEncoder());
        var server = new SembleMcpServer(cache, defaultSource: null);
        var text = await server.CallToolAsync("find_related", new Dictionary<string, object?>
        {
            ["file_path"] = "src/foo.py", ["line"] = 10,
        });
        Assert.Contains("No repo specified", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_When_Index_Build_Fails_Returns_Failed_To_Index_Message()
    {
        var cache = new IndexCache(new MockEncoder())
        {
            FromGit = (_, _, _) => throw new InvalidOperationException("clone failed"),
        };
        var server = new SembleMcpServer(cache);
        var text = await server.CallToolAsync("search", new Dictionary<string, object?>
        {
            ["query"] = "foo", ["repo"] = "https://github.com/x/y",
        });
        Assert.Contains("Failed to index", text, StringComparison.Ordinal);
        Assert.Contains("clone failed", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindRelated_When_Index_Build_Fails_Returns_Failed_To_Index_Message()
    {
        var cache = new IndexCache(new MockEncoder())
        {
            FromGit = (_, _, _) => throw new InvalidOperationException("clone failed"),
        };
        var server = new SembleMcpServer(cache);
        var text = await server.CallToolAsync("find_related", new Dictionary<string, object?>
        {
            ["file_path"] = "src/foo.py", ["line"] = 1, ["repo"] = "https://github.com/x/y",
        });
        Assert.Contains("Failed to index", text, StringComparison.Ordinal);
        Assert.Contains("clone failed", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_With_Results_Renders_File_And_Score()
    {
        var index = FakeIndex.Build(new[] { MakeChunk("def bar(): pass", "src/bar.py") });
        var server = new SembleMcpServer(CacheFor(index), defaultSource: "/some/path");
        var text = await server.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "bar" });
        Assert.Contains("bar", text, StringComparison.Ordinal);
        Assert.Matches(@"score=\d+\.\d{3}", text);
    }

    [Fact]
    public async Task Search_With_No_Matches_Returns_Friendly_Message()
    {
        var index = FakeIndex.Build(new[] { MakeChunk("def bar(): pass", "src/bar.py") });
        var server = new SembleMcpServer(CacheFor(index), defaultSource: "/some/path");
        var text = await server.CallToolAsync("search", new Dictionary<string, object?>
        {
            ["query"] = "zzzznonexistentterm",
            ["mode"] = "bm25",
        });
        Assert.Contains("No results found", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindRelated_With_Results_Renders_File_And_Score()
    {
        var index = FakeIndex.Build(new[]
        {
            MakeChunk("class Foo: pass", "src/foo.py"),
            MakeChunk("class Other: pass", "src/other.py"),
        });
        var server = new SembleMcpServer(CacheFor(index), defaultSource: "/some/path");
        var text = await server.CallToolAsync("find_related", new Dictionary<string, object?>
        {
            ["file_path"] = "src/foo.py", ["line"] = 1,
        });
        Assert.Contains("src/foo.py:1", text, StringComparison.Ordinal);
        Assert.Matches(@"score=\d+\.\d{3}", text);
    }

    [Fact]
    public async Task FindRelated_With_No_Related_Chunks_Returns_Friendly_Message()
    {
        var index = FakeIndex.Build(new[] { MakeChunk("class Foo: pass", "src/foo.py") });
        var server = new SembleMcpServer(CacheFor(index), defaultSource: "/some/path");
        var text = await server.CallToolAsync("find_related", new Dictionary<string, object?>
        {
            ["file_path"] = "src/foo.py", ["line"] = 1,
        });
        Assert.Contains("No related chunks found", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindRelated_With_Unknown_File_Returns_Friendly_Message()
    {
        var index = FakeIndex.Build(new[] { MakeChunk("class Foo: pass", "src/foo.py") });
        var server = new SembleMcpServer(CacheFor(index), defaultSource: "/some/path");
        var text = await server.CallToolAsync("find_related", new Dictionary<string, object?>
        {
            ["file_path"] = "src/unknown.py", ["line"] = 1,
        });
        Assert.Contains("No chunk found", text, StringComparison.Ordinal);
    }
}
