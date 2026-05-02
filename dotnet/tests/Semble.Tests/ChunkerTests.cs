using Semble.Index;
using Xunit;

namespace Semble.Tests;

public class ChunkerTests : IDisposable
{
    private readonly string _tmp;

    public ChunkerTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "semble-chunker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void ChunkLines_Empty_Returns_Empty()
    {
        Assert.Empty(Chunker.ChunkLines("", "empty.py", "python"));
    }

    [Fact]
    public void ChunkLines_Real_Input_Yields_Multiple_Chunks_Starting_At_Line_One()
    {
        var content = string.Join("\n", Enumerable.Range(0, 10).Select(i => $"line {i}"));
        var chunks = Chunker.ChunkLines(content, "test.py", "python", maxLines: 5, overlapLines: 1);
        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.False(string.IsNullOrWhiteSpace(c.Content)));
        Assert.Equal(1, chunks[0].StartLine);
    }

    [Fact]
    public void ChunkFile_Nonexistent_Path_Returns_Empty_List()
    {
        var chunks = Chunker.ChunkFile("/nonexistent/file.py");
        Assert.NotNull(chunks);
        Assert.Empty(chunks);
    }

    [Fact]
    public void ChunkFile_Whitespace_Only_Returns_Empty()
    {
        var path = Path.Combine(_tmp, "empty.py");
        File.WriteAllText(path, "   \n\n  ");
        var chunks = Chunker.ChunkFile(path);
        Assert.NotNull(chunks);
        Assert.Empty(chunks);
    }

    [Fact]
    public void ChunkFile_Unknown_Extension_Still_Returns_List()
    {
        var path = Path.Combine(_tmp, "file.xyz");
        File.WriteAllText(path, string.Concat(Enumerable.Repeat("hello world\n", 5)));
        var chunks = Chunker.ChunkFile(path);
        Assert.NotNull(chunks);
        // Unknown extensions still chunk via the line-based fallback.
        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.Null(c.Language));
    }

    [Fact(Skip = "tree-sitter chunker integration deferred to a follow-up chunk")]
    public void ChunkFile_Py_Produces_Sorted_Chunks()
    {
        // Mirrors test_chunk_file_py_produces_sorted_chunks. The .NET port currently
        // always uses the line-based fallback; this assertion will be re-enabled once
        // tree-sitter / Chonkie equivalent is wired up.
    }

    // The Python suite has three "Chonkie-fallback" parametrise cases (raises,
    // empty, whitespace-only). The .NET port has no Chonkie integration yet, so
    // chunk_source unconditionally takes the line-based fallback path the upstream
    // tests exercise. We verify the same observable behaviour: a non-trivial
    // source is chunked and produces non-empty content chunks.
    [Theory]
    [InlineData("def foo():\n    pass\n")]
    public void ChunkSource_Always_Falls_Back_To_Line_Based_Chunking(string source)
    {
        var chunks = Chunker.ChunkSource(source, "foo.py", "python");
        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.False(string.IsNullOrWhiteSpace(c.Content)));
    }

    [Fact]
    public void ChunkSource_Whitespace_Only_Returns_Empty()
    {
        Assert.Empty(Chunker.ChunkSource("   \n\n", "foo.py", "python"));
    }

    [Fact]
    public void FilterExtensions_Explicit_Returns_Caller_Set()
    {
        var explicitSet = new HashSet<string>(StringComparer.Ordinal) { ".py", ".ts" };
        var result = FileWalker.FilterExtensions(explicitSet, includeTextFiles: false);
        Assert.Equal(explicitSet, result);
    }
}
