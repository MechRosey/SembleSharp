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

    // C# equivalent of the upstream tree-sitter / py-tree-sitter test
    // (test_chunk_file_py_produces_sorted_chunks). The .NET port uses Roslyn for
    // C# specifically and tree-sitter for the rest (tree-sitter integration is
    // wired up in a follow-up commit).
    [Fact]
    public void ChunkFile_Cs_Produces_Sorted_Chunks()
    {
        var path = Path.Combine(_tmp, "Sample.cs");
        File.WriteAllText(path,
            "namespace Sample\n" +
            "{\n" +
            "    public class A\n" +
            "    {\n" +
            "        public int Add(int a, int b) => a + b;\n" +
            "        public int Subtract(int a, int b) => a - b;\n" +
            "    }\n" +
            "}\n");
        var chunks = Chunker.ChunkFile(path);
        Assert.NotEmpty(chunks);
        var startLines = chunks.Select(c => c.StartLine).ToList();
        Assert.Equal(startLines.OrderBy(x => x).ToList(), startLines);
        Assert.All(chunks, c => Assert.Equal("csharp", c.Language));
    }

    [Fact]
    public void RoslynChunker_Splits_Large_Class_Across_Multiple_Chunks()
    {
        // Build a class with a few methods large enough to exceed a tight chunk size.
        var methods = string.Concat(Enumerable.Range(0, 5).Select(i =>
            $"    public void M{i}() {{ var s = \"method {i} body padding to push us over the budget\"; }}\n"));
        var source = $"namespace Big {{\n  public class C {{\n{methods}  }}\n}}\n";

        var chunks = RoslynChunker.TryChunkCSharp(source, "Big.cs", "csharp", chunkSize: 80);
        Assert.NotNull(chunks);
        Assert.True(chunks!.Count >= 2,
            $"expected splitting across at least 2 chunks; got {chunks.Count}");
        var startLines = chunks.Select(c => c.StartLine).ToList();
        Assert.Equal(startLines.OrderBy(x => x).ToList(), startLines);
    }

    [Fact]
    public void RoslynChunker_Returns_Null_For_Source_With_No_Members()
    {
        // Just usings, no namespaces / types / top-level statements that would
        // surface as splittable members.
        Assert.Null(RoslynChunker.TryChunkCSharp("using System;\nusing System.IO;\n", "U.cs", "csharp"));
    }

    [Fact]
    public void ChunkSource_Csharp_Routes_Through_Roslyn_When_Possible_And_Falls_Back_Otherwise()
    {
        // Real class → Roslyn chunker produces a chunk whose content starts at the class.
        var roslynChunks = Chunker.ChunkSource(
            "public class Foo { public int X => 1; }\n", "Foo.cs", "csharp");
        Assert.Single(roslynChunks);
        Assert.StartsWith("public class Foo", roslynChunks[0].Content);

        // Just usings → no Roslyn-splittable members → falls back to line chunker.
        var fallback = Chunker.ChunkSource(
            "using System;\nusing System.IO;\n", "U.cs", "csharp");
        Assert.NotEmpty(fallback);
        Assert.Equal(1, fallback[0].StartLine);
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
