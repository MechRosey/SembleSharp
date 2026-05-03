using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Semble.Index;
using Semble.Index.Extractors;
using Xunit;

namespace Semble.Tests;

public class OpenXmlWordExtractorTests : IDisposable
{
    private readonly string _tmp;

    public OpenXmlWordExtractorTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "semble-docx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Produce a real .docx on disk via the OpenXml builder API. Each entry is
    /// rendered as one paragraph; the first element is the style id (null for
    /// plain paragraph), the second is the text.
    /// </summary>
    private string WriteDocx(params (string? Style, string Text)[] paragraphs)
    {
        var path = Path.Combine(_tmp, $"doc-{Guid.NewGuid():N}.docx");
        using (var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            foreach (var (style, text) in paragraphs)
            {
                var para = new Paragraph();
                if (style is not null)
                {
                    para.AppendChild(new ParagraphProperties(
                        new ParagraphStyleId() { Val = style }));
                }
                para.AppendChild(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
                body.AppendChild(para);
            }
            main.Document = new Document(body);
            main.Document.Save();
        }
        return path;
    }

    [Fact]
    public void Available_Without_External_Tools()
    {
        Assert.True(new OpenXmlWordExtractor().IsAvailable);
    }

    [Fact]
    public void Heading_Style_Renders_As_Markdown_Heading()
    {
        var path = WriteDocx(
            ("Heading1", "Architecture"),
            (null, "First paragraph."),
            ("Heading2", "Storage"),
            (null, "Persistence layer."));

        var result = new OpenXmlWordExtractor().ExtractText(path);
        Assert.Equal("markdown", result.Language);
        Assert.Contains("# Architecture", result.Text, StringComparison.Ordinal);
        Assert.Contains("## Storage", result.Text, StringComparison.Ordinal);
        Assert.Contains("First paragraph.", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Title_And_Subtitle_Map_To_H1_And_H2()
    {
        var path = WriteDocx(
            ("Title", "Project"),
            ("Subtitle", "Tagline"),
            (null, "Body."));
        var text = new OpenXmlWordExtractor().ExtractText(path).Text;
        Assert.Contains("# Project", text, StringComparison.Ordinal);
        Assert.Contains("## Tagline", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Heading_Levels_Beyond_Six_Are_Treated_As_Plain_Paragraphs()
    {
        // Word allows Heading1..Heading9; markdown caps at 6, so styles 7..9
        // fall through to plain paragraph rendering rather than emitting
        // "####### something" (which markdown doesn't recognise as a heading).
        var path = WriteDocx(("Heading7", "Way too deep"));
        var text = new OpenXmlWordExtractor().ExtractText(path).Text;
        Assert.DoesNotContain("#", text, StringComparison.Ordinal);
        Assert.Contains("Way too deep", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_Body_Returns_Empty_Text()
    {
        var path = Path.Combine(_tmp, "empty.docx");
        using (var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body());
        }
        var result = new OpenXmlWordExtractor().ExtractText(path);
        Assert.Equal("markdown", result.Language);
        Assert.Equal("", result.Text);
    }

    [Fact]
    public void Corrupted_File_Throws_InvalidOperationException()
    {
        var path = Path.Combine(_tmp, "broken.docx");
        File.WriteAllText(path, "not actually a docx file");
        Assert.Throws<InvalidOperationException>(() => new OpenXmlWordExtractor().ExtractText(path));
    }

    [Fact]
    public void ChunkFile_Routes_Docx_Through_OpenXml_When_MarkItDown_Unavailable()
    {
        var path = WriteDocx(
            ("Heading1", "Setup"),
            (null, "Run dotnet build."),
            ("Heading1", "Use"),
            (null, "Open the CLI."));

        // Force the registry to use only the openxml-word extractor for this
        // test so we don't depend on whether markitdown is installed.
        var saved = TextExtractors.Current;
        try
        {
            TextExtractors.Current = new TextExtractors(new Dictionary<string, IReadOnlyList<ITextExtractor>>
            {
                [".docx"] = new ITextExtractor[] { new OpenXmlWordExtractor() },
            });

            var chunks = Chunker.ChunkFile(path);
            Assert.Equal(2, chunks.Count);
            // markdown output → MarkdownChunker → Heading locator with the
            // doc's heading path.
            Assert.All(chunks, c => Assert.IsType<Locator.Heading>(c.Locator));
            var paths = chunks.Select(c => ((Locator.Heading)c.Locator!).Path).ToList();
            Assert.Equal(new[] { "Setup" }, paths[0]);
            Assert.Equal(new[] { "Use" }, paths[1]);
        }
        finally
        {
            TextExtractors.Current = saved;
        }
    }

    [Fact]
    public void Default_Registry_Lists_OpenXml_After_MarkItDown_For_Docx()
    {
        // Sanity check: docx has a managed fallback so it indexes on hosts
        // with no Python; non-docx Office formats still depend on markitdown.
        Assert.Contains(".docx", TextExtractors.Default.RegisteredExtensions);
        // The For() method picks the first available; on a machine with no
        // markitdown installed, the openxml-word fallback should be returned.
        var picked = TextExtractors.Default.For("test.docx");
        Assert.NotNull(picked);
    }
}
