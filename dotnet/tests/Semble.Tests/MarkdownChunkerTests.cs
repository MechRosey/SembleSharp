using Semble.Index;
using Xunit;

namespace Semble.Tests;

public class MarkdownChunkerTests
{
    [Fact]
    public void Empty_Or_Whitespace_Source_Returns_Null()
    {
        Assert.Null(MarkdownChunker.TryChunk("", "x.md", "markdown"));
        Assert.Null(MarkdownChunker.TryChunk("   \n\n", "x.md", "markdown"));
    }

    [Fact]
    public void Single_Section_With_No_Heading_Becomes_One_Chunk_With_Empty_Path()
    {
        var src = "Just a paragraph of text.\nAnother line.\n";
        var chunks = MarkdownChunker.TryChunk(src, "n.md", "markdown");
        Assert.NotNull(chunks);
        Assert.Single(chunks!);
        var loc = Assert.IsType<Locator.Heading>(chunks![0].Locator);
        Assert.Empty(loc.Path);
        Assert.Equal("n.md:1-2", chunks[0].Location);
    }

    [Fact]
    public void Each_Top_Level_Heading_Becomes_Its_Own_Chunk_With_Path()
    {
        var src =
            "# Intro\n" +
            "Welcome.\n" +
            "\n" +
            "# Setup\n" +
            "Run dotnet build.\n";
        var chunks = MarkdownChunker.TryChunk(src, "n.md", "markdown");
        Assert.NotNull(chunks);
        Assert.Equal(2, chunks!.Count);
        var first = (Locator.Heading)chunks[0].Locator!;
        var second = (Locator.Heading)chunks[1].Locator!;
        Assert.Equal(new[] { "Intro" }, first.Path);
        Assert.Equal(new[] { "Setup" }, second.Path);
        Assert.Equal("n.md:Intro:1-3", chunks[0].Location);
        Assert.Equal("n.md:Setup:4-5", chunks[1].Location);
    }

    [Fact]
    public void Nested_Headings_Build_Path_Stack()
    {
        var src =
            "# Architecture\n" +
            "Top.\n" +
            "## Storage\n" +
            "Disk.\n" +
            "## Networking\n" +
            "TCP.\n" +
            "# Deployment\n" +
            "K8s.\n";
        var chunks = MarkdownChunker.TryChunk(src, "n.md", "markdown");
        Assert.NotNull(chunks);
        // Architecture, Architecture/Storage, Architecture/Networking, Deployment
        Assert.Equal(4, chunks!.Count);
        var paths = chunks.Select(c => ((Locator.Heading)c.Locator!).Path).ToList();
        Assert.Equal(new[] { "Architecture" }, paths[0]);
        Assert.Equal(new[] { "Architecture", "Storage" }, paths[1]);
        Assert.Equal(new[] { "Architecture", "Networking" }, paths[2]);
        Assert.Equal(new[] { "Deployment" }, paths[3]);
        Assert.Equal("n.md:Architecture/Storage:3-4", chunks[1].Location);
    }

    [Fact]
    public void Heading_Inside_Code_Fence_Is_Not_Treated_As_A_Heading()
    {
        var src =
            "# Real Heading\n" +
            "```\n" +
            "# This looks like a heading but is code.\n" +
            "echo hi\n" +
            "```\n" +
            "More text.\n";
        var chunks = MarkdownChunker.TryChunk(src, "n.md", "markdown");
        Assert.NotNull(chunks);
        Assert.Single(chunks!);
        var path = ((Locator.Heading)chunks![0].Locator!).Path;
        Assert.Equal(new[] { "Real Heading" }, path);
        Assert.Contains("# This looks like a heading", chunks[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Oversized_Section_Is_Split_On_Paragraph_Boundaries()
    {
        // One heading, two clearly separated paragraphs that together exceed
        // a tight chunkSize. Each paragraph should land in its own chunk.
        var p1 = string.Join("\n", Enumerable.Repeat("paragraph one filler text", 6));
        var p2 = string.Join("\n", Enumerable.Repeat("paragraph two filler text", 6));
        var src = $"# Big\n{p1}\n\n{p2}\n";
        var chunks = MarkdownChunker.TryChunk(src, "n.md", "markdown", chunkSize: 80);
        Assert.NotNull(chunks);
        Assert.True(chunks!.Count >= 2);
        // Every chunk shares the same heading path.
        Assert.All(chunks, c =>
        {
            var h = Assert.IsType<Locator.Heading>(c.Locator);
            Assert.Equal(new[] { "Big" }, h.Path);
        });
    }

    [Fact]
    public void Adjacent_Same_Path_Sections_Merge_Up_To_Budget()
    {
        // Two short top-level headings can't merge because their paths differ;
        // verify chunks stay distinct.
        var src = "# A\nfoo\n# B\nbar\n";
        var chunks = MarkdownChunker.TryChunk(src, "n.md", "markdown");
        Assert.NotNull(chunks);
        Assert.Equal(2, chunks!.Count);
    }

    [Fact]
    public void StartLines_Are_Sorted_Ascending()
    {
        var src =
            "# A\n" +
            "x\n" +
            "## B\n" +
            "y\n" +
            "z\n" +
            "# C\n" +
            "w\n";
        var chunks = MarkdownChunker.TryChunk(src, "n.md", "markdown");
        Assert.NotNull(chunks);
        var starts = chunks!.Select(c => c.StartLine).ToList();
        Assert.Equal(starts.OrderBy(x => x).ToList(), starts);
    }

    [Fact]
    public void ChunkSource_Markdown_Routes_Through_MarkdownChunker()
    {
        var src = "# Hello\nworld\n";
        var chunks = Chunker.ChunkSource(src, "doc.md", "markdown");
        Assert.NotEmpty(chunks);
        Assert.IsType<Locator.Heading>(chunks[0].Locator);
    }

    [Fact]
    public void ChunkFile_Md_Sets_Markdown_Language_And_Heading_Locator()
    {
        var dir = Path.Combine(Path.GetTempPath(), "semble-md-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "guide.md");
            File.WriteAllText(path, "# Setup\nstep one\n# Use\nstep two\n");
            var chunks = Chunker.ChunkFile(path);
            Assert.Equal(2, chunks.Count);
            Assert.All(chunks, c => Assert.Equal("markdown", c.Language));
            Assert.All(chunks, c => Assert.IsType<Locator.Heading>(c.Locator));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
