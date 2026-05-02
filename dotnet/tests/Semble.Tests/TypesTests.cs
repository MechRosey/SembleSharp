using Xunit;

namespace Semble.Tests;

public class ChunkTests
{
    [Fact]
    public void Location_Formats_As_FilePath_StartLine_EndLine()
    {
        var chunk = new Chunk("def foo(): ...", "src/module.py", 12, 18, "python");
        Assert.Equal("src/module.py:12-18", chunk.Location);
    }

    [Fact]
    public void Records_Compare_By_Value()
    {
        var a = new Chunk("x", "a.py", 1, 1);
        var b = new Chunk("x", "a.py", 1, 1);
        var c = new Chunk("x", "a.py", 1, 2);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Language_Is_Optional()
    {
        var chunk = new Chunk("x", "a.py", 1, 1);
        Assert.Null(chunk.Language);
    }
}

public class SearchModeTests
{
    [Theory]
    [InlineData(SearchMode.Hybrid, "hybrid")]
    [InlineData(SearchMode.Semantic, "semantic")]
    [InlineData(SearchMode.Bm25, "bm25")]
    public void ToWire_Roundtrips_With_ParseMode(SearchMode mode, string wire)
    {
        Assert.Equal(wire, mode.ToWire());
        Assert.Equal(mode, SearchModeExtensions.ParseMode(wire));
    }

    [Fact]
    public void ParseMode_Throws_On_Unknown_Value()
    {
        Assert.Throws<ArgumentException>(() => SearchModeExtensions.ParseMode("vector"));
    }
}

public class IndexStatsTests
{
    [Fact]
    public void Defaults_Are_Zero_And_Empty()
    {
        var stats = new IndexStats();
        Assert.Equal(0, stats.IndexedFiles);
        Assert.Equal(0, stats.TotalChunks);
        Assert.Empty(stats.Languages);
    }

    [Fact]
    public void Populated_Stats_Round_Trip_Languages()
    {
        var stats = new IndexStats(2, 5, new Dictionary<string, int> { ["python"] = 3, ["javascript"] = 2 });
        Assert.Equal(2, stats.IndexedFiles);
        Assert.Equal(5, stats.TotalChunks);
        Assert.Equal(3, stats.Languages["python"]);
        Assert.Equal(2, stats.Languages["javascript"]);
    }
}
