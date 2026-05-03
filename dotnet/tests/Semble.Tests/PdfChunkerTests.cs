using Semble.Index;
using Xunit;

namespace Semble.Tests;

public class PdfChunkerTests
{
    [Fact]
    public void Empty_PageBreaks_Returns_Null()
    {
        Assert.Null(PdfChunker.TryChunk("anything", Array.Empty<int>(), "x.pdf"));
    }

    [Fact]
    public void Empty_Text_Returns_Null()
    {
        Assert.Null(PdfChunker.TryChunk("", new[] { 0 }, "x.pdf"));
    }

    [Fact]
    public void Single_Page_Becomes_One_Chunk_With_Pages_Locator()
    {
        var text = "page 1 line 1\npage 1 line 2\n";
        var chunks = PdfChunker.TryChunk(text, new[] { 0 }, "doc.pdf");
        Assert.NotNull(chunks);
        Assert.Single(chunks!);
        var loc = Assert.IsType<Locator.Pages>(chunks![0].Locator);
        Assert.Equal(1, loc.Start);
        Assert.Equal(1, loc.End);
        Assert.Equal("doc.pdf:p1", chunks[0].Location);
        Assert.Equal("pdf", chunks[0].Language);
    }

    [Fact]
    public void Three_Pages_Yield_One_Chunk_Each_With_Sequential_Page_Numbers()
    {
        // Each page is a single short line plus a form feed.
        var text = "first page text\n\fsecond page text\n\fthird page text\n";
        // Page breaks: 0 (page 1), after first \f (page 2), after second \f (page 3)
        var p1 = 0;
        var p2 = text.IndexOf('\f') + 1;
        var p3 = text.IndexOf('\f', p2) + 1;
        var chunks = PdfChunker.TryChunk(text, new[] { p1, p2, p3 }, "doc.pdf");
        Assert.NotNull(chunks);
        Assert.Equal(3, chunks!.Count);
        var pageNumbers = chunks.Select(c => ((Locator.Pages)c.Locator!).Start).ToList();
        Assert.Equal(new[] { 1, 2, 3 }, pageNumbers);
        Assert.Contains("first page text", chunks[0].Content, StringComparison.Ordinal);
        Assert.Contains("second page text", chunks[1].Content, StringComparison.Ordinal);
        Assert.Contains("third page text", chunks[2].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_Page_Splits_Into_Multiple_Chunks_That_All_Carry_Same_Page_Number()
    {
        // 200 lines on a single page → multiple chunks at maxLines=50.
        var text = string.Concat(Enumerable.Range(1, 200).Select(i => $"line {i}\n"));
        var chunks = PdfChunker.TryChunk(text, new[] { 0 }, "big.pdf");
        Assert.NotNull(chunks);
        Assert.True(chunks!.Count >= 4, $"expected ≥4 sub-chunks for 200 lines / 50 per chunk; got {chunks.Count}");
        Assert.All(chunks, c =>
        {
            var loc = Assert.IsType<Locator.Pages>(c.Locator);
            Assert.Equal(1, loc.Start);
            Assert.Equal(1, loc.End);
        });
        // Sub-chunks should have ascending start lines.
        var starts = chunks.Select(c => c.StartLine).ToList();
        Assert.Equal(starts.OrderBy(x => x).ToList(), starts);
    }

    [Fact]
    public void Chunks_Never_Cross_Page_Boundaries()
    {
        // Two short pages totalling less than the default 50-line window.
        // Even though they'd fit in one window by line count, they MUST stay
        // in separate chunks because they're on different pages.
        var p1Text = "alpha line 1\nalpha line 2\n";
        var p2Text = "beta line 1\nbeta line 2\n";
        var text = p1Text + "\f" + p2Text;
        var p1Start = 0;
        var p2Start = p1Text.Length + 1; // position after the \f
        var chunks = PdfChunker.TryChunk(text, new[] { p1Start, p2Start }, "two.pdf");
        Assert.NotNull(chunks);
        Assert.Equal(2, chunks!.Count);
        Assert.Contains("alpha line 1", chunks[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("beta", chunks[0].Content, StringComparison.Ordinal);
        Assert.Contains("beta line 1", chunks[1].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha", chunks[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Whitespace_Only_Pages_Are_Skipped()
    {
        // A blank page between two real pages — no chunk should be emitted for the blank.
        var text = "real first page\n\f   \n\f\nreal third page\n";
        var p1 = 0;
        var p2 = text.IndexOf('\f') + 1;
        var p3 = text.IndexOf('\f', p2) + 1;
        var chunks = PdfChunker.TryChunk(text, new[] { p1, p2, p3 }, "blank.pdf");
        Assert.NotNull(chunks);
        Assert.Equal(2, chunks!.Count);
        var pages = chunks.Select(c => ((Locator.Pages)c.Locator!).Start).ToList();
        Assert.Equal(new[] { 1, 3 }, pages); // page 2 dropped
    }

    [Fact]
    public void StartLine_And_EndLine_Are_1_Indexed_Relative_To_Full_Extracted_Text()
    {
        var text =
            "page1 line1\n" +     // text line 1
            "page1 line2\n" +     // text line 2
            "\f" +                // form feed (no newline added — pdftotext format)
            "page2 line1\n" +     // text line 3
            "page2 line2\n";      // text line 4
        var p1 = 0;
        var p2 = text.IndexOf('\f') + 1;
        var chunks = PdfChunker.TryChunk(text, new[] { p1, p2 }, "lines.pdf");
        Assert.NotNull(chunks);
        Assert.Equal(2, chunks!.Count);
        // Both pages should have ascending, sensible line ranges.
        Assert.True(chunks[0].StartLine < chunks[1].StartLine);
        Assert.True(chunks[0].StartLine >= 1);
    }
}

public class PdfExtractorPipelineTests : IDisposable
{
    private readonly TextExtractors _saved;
    private readonly string _tmp;

    public PdfExtractorPipelineTests()
    {
        _saved = TextExtractors.Current;
        _tmp = Path.Combine(Path.GetTempPath(), "semble-pdfpipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        TextExtractors.Current = _saved;
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
    }

    private sealed class FakePagedExtractor : ITextExtractor
    {
        public string Name => "fake-paged";
        public bool IsAvailable => true;
        public ExtractedText ExtractText(string filePath)
        {
            // 3 pages, separated by \f exactly like pdftotext would emit.
            var text = "page one\n\fpage two\n\fpage three\n";
            int p2 = text.IndexOf('\f') + 1;
            int p3 = text.IndexOf('\f', p2) + 1;
            return new ExtractedText(text, "text", new[] { 0, p2, p3 });
        }
    }

    [Fact]
    public void ChunkFile_Pdf_With_PageBreaks_Stamps_Pages_Locator_On_Every_Chunk()
    {
        TextExtractors.Current = new TextExtractors(new Dictionary<string, IReadOnlyList<ITextExtractor>>
        {
            [".pdf"] = new ITextExtractor[] { new FakePagedExtractor() },
        });

        var pdf = Path.Combine(_tmp, "doc.pdf");
        File.WriteAllText(pdf, "stub");

        var chunks = Chunker.ChunkFile(pdf);
        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.IsType<Locator.Pages>(c.Locator));
        var pages = chunks.Select(c => ((Locator.Pages)c.Locator!).Start).ToList();
        Assert.Equal(new[] { 1, 2, 3 }, pages);
        // ChunkFile receives the absolute path (the file walker yields
        // absolute paths), so Location starts with the absolute file path.
        Assert.EndsWith("doc.pdf:p1", chunks[0].Location, StringComparison.Ordinal);
        Assert.EndsWith("doc.pdf:p2", chunks[1].Location, StringComparison.Ordinal);
        Assert.EndsWith("doc.pdf:p3", chunks[2].Location, StringComparison.Ordinal);
    }

    [Fact]
    public void PdftotextExtractor_PostProcess_Produces_PageBreaks_From_Form_Feeds()
    {
        // Skip if real pdftotext isn't on the host — this just verifies the
        // PostProcess hook on a synthesised stdout, not the binary itself.
        var ext = new Semble.Index.Extractors.PdftotextExtractor();
        var method = typeof(Semble.Index.Extractors.SubprocessExtractor)
            .GetMethod("PostProcess", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var stdout = "alpha\n\fbeta\n\fgamma\n";
        var result = (ExtractedText)method.Invoke(ext, new object[] { stdout })!;
        Assert.NotNull(result.PageBreaks);
        // \f at indices 6 and 12 → the first character of pages 2 and 3 are
        // at indices 7 and 13 respectively.
        Assert.Equal(new[] { 0, 7, 13 }, result.PageBreaks);
    }
}
