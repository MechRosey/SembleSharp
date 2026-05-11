using Xunit;

namespace Semble.Tests;

public class ChunkTests
{
    [Fact]
    public void Location_Formats_As_FilePath_StartLine_EndLine_When_Locator_Is_Null()
    {
        var chunk = new Chunk("def foo(): ...", "src/module.py", 12, 18, "python");
        Assert.Equal("src/module.py:12-18", chunk.Location);
        Assert.Null(chunk.Locator);
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

    [Fact]
    public void Pages_Locator_Renders_As_Single_Page_Or_Range()
    {
        var single = new Chunk("text", "paper.pdf", 1, 42, Locator: new Locator.Pages(3, 3));
        Assert.Equal("paper.pdf:p3", single.Location);

        var range = new Chunk("text", "paper.pdf", 1, 80, Locator: new Locator.Pages(3, 5));
        Assert.Equal("paper.pdf:p3-5", range.Location);
    }

    [Fact]
    public void Slide_Locator_Renders_With_Slide_Prefix()
    {
        var c = new Chunk("text", "deck.pptx", 1, 5, Locator: new Locator.Slide(12));
        Assert.Equal("deck.pptx:slide12", c.Location);
    }

    [Fact]
    public void Sheet_Locator_Renders_With_Sheet_Bang_Range()
    {
        var c = new Chunk("text", "book.xlsx", 1, 10, Locator: new Locator.Sheet("Sheet1", "A1:C10"));
        Assert.Equal("book.xlsx:Sheet1!A1:C10", c.Location);
    }

    [Fact]
    public void Heading_Locator_Renders_Path_Slash_Lines()
    {
        var c = new Chunk("text", "notes.md", 42, 58,
            Locator: new Locator.Heading(new[] { "Architecture", "Storage" }));
        Assert.Equal("notes.md:Architecture/Storage:42-58", c.Location);
    }

    [Fact]
    public void Heading_Locator_With_Empty_Path_Renders_Just_Line_Range()
    {
        var c = new Chunk("text", "notes.md", 1, 5, Locator: new Locator.Heading(Array.Empty<string>()));
        Assert.Equal("notes.md:1-5", c.Location);
    }

    [Fact]
    public void Heading_Locator_Equals_Compares_Path_By_Sequence()
    {
        var a = new Locator.Heading(new[] { "A", "B" });
        var b = new Locator.Heading(new List<string> { "A", "B" });
        var c = new Locator.Heading(new[] { "A", "B", "C" });
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
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

public class FormattingTests
{
    [Theory]
    [InlineData("https://github.com/x/y", true)]
    [InlineData("git@github.com:x/y", true)]
    [InlineData("/local/path", false)]
    [InlineData("./rel", false)]
    [InlineData("file:///foo", true)]
    public void IsGitUrl_Matches_Python(string path, bool expected)
    {
        Assert.Equal(expected, Formatting.IsGitUrl(path));
    }

    [Fact]
    public void FormatResults_Matches_Python_Output()
    {
        var chunk = new Chunk(
            Content: "def foo():\n    pass",
            FilePath: "src/foo.py",
            StartLine: 1,
            EndLine: 2,
            Language: "python");
        var res = new SearchResult(chunk, 0.9, SearchMode.Hybrid);
        var actual = Formatting.FormatResults("Header", new[] { res });
        const string expected =
            "Header\n\n## 1. src/foo.py:1-2  [score=0.900]\n```\ndef foo():\n    pass\n```\n";
        Assert.Equal(expected, actual);
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
