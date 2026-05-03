using Semble.Index;
using Xunit;

namespace Semble.Tests;

internal sealed class FakeExtractor : ITextExtractor
{
    public string Name { get; init; } = "fake";
    public bool IsAvailable { get; init; } = true;
    public string OutputText { get; init; } = "";
    public string OutputLanguage { get; init; } = "text";
    public Action<string>? OnExtract { get; init; }
    public Exception? Throws { get; init; }

    public ExtractedText ExtractText(string filePath)
    {
        OnExtract?.Invoke(filePath);
        if (Throws is not null)
            throw Throws;
        return new ExtractedText(OutputText, OutputLanguage);
    }
}

public class TextExtractorRegistryTests
{
    [Fact]
    public void For_Returns_First_Available_Extractor()
    {
        var unavailable = new FakeExtractor { Name = "u", IsAvailable = false };
        var available = new FakeExtractor { Name = "a", IsAvailable = true };
        var reg = new TextExtractors(new Dictionary<string, IReadOnlyList<ITextExtractor>>
        {
            [".pdf"] = new ITextExtractor[] { unavailable, available },
        });

        var picked = reg.For("/some/file.pdf");
        Assert.Same(available, picked);
    }

    [Fact]
    public void For_Returns_Null_When_No_Extension_Registered()
    {
        var reg = new TextExtractors(new Dictionary<string, IReadOnlyList<ITextExtractor>>());
        Assert.Null(reg.For("/some/file.py"));
    }

    [Fact]
    public void For_Returns_Null_When_No_Extractor_Is_Available()
    {
        var unavailable = new FakeExtractor { IsAvailable = false };
        var reg = new TextExtractors(new Dictionary<string, IReadOnlyList<ITextExtractor>>
        {
            [".pdf"] = new ITextExtractor[] { unavailable },
        });
        Assert.Null(reg.For("/x.pdf"));
    }

    [Fact]
    public void For_Lookup_Is_Case_Insensitive_On_Extension()
    {
        var available = new FakeExtractor { IsAvailable = true };
        var reg = new TextExtractors(new Dictionary<string, IReadOnlyList<ITextExtractor>>
        {
            [".pdf"] = new ITextExtractor[] { available },
        });
        Assert.Same(available, reg.For("/X.PDF"));
    }

    [Fact]
    public void Default_Registry_Covers_Pdf_And_Modern_Office()
    {
        Assert.Contains(".pdf", TextExtractors.Default.RegisteredExtensions);
        Assert.Contains(".docx", TextExtractors.Default.RegisteredExtensions);
        Assert.Contains(".xlsx", TextExtractors.Default.RegisteredExtensions);
        Assert.Contains(".pptx", TextExtractors.Default.RegisteredExtensions);
    }
}

public class ChunkerExtractorIntegrationTests : IDisposable
{
    private readonly TextExtractors _saved;
    private readonly string _tmp;

    public ChunkerExtractorIntegrationTests()
    {
        _saved = TextExtractors.Current;
        _tmp = Path.Combine(Path.GetTempPath(), "semble-extractor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        TextExtractors.Current = _saved;
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void ChunkFile_Routes_Pdf_Through_Registered_Extractor()
    {
        var captured = new List<string>();
        var fake = new FakeExtractor
        {
            OutputText = "extracted text from pdf\nsecond line\n",
            OutputLanguage = "text",
            OnExtract = path => captured.Add(path),
        };
        TextExtractors.Current = new TextExtractors(new Dictionary<string, IReadOnlyList<ITextExtractor>>
        {
            [".pdf"] = new ITextExtractor[] { fake },
        });

        // Touch the file (extractor doesn't actually read it, but ChunkFile checks no IO error path).
        var pdf = Path.Combine(_tmp, "doc.pdf");
        File.WriteAllText(pdf, "%PDF-1.4 stub");

        var chunks = Chunker.ChunkFile(pdf);
        Assert.NotEmpty(chunks);
        Assert.Single(captured);
        Assert.Equal(pdf, captured[0]);
        Assert.Contains("extracted text from pdf", chunks[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ChunkFile_Markdown_Output_Routes_Through_Markdown_Chunker()
    {
        var fake = new FakeExtractor
        {
            OutputText = "# Heading\n\nFirst paragraph.\n",
            OutputLanguage = "markdown",
        };
        TextExtractors.Current = new TextExtractors(new Dictionary<string, IReadOnlyList<ITextExtractor>>
        {
            [".pdf"] = new ITextExtractor[] { fake },
        });

        var pdf = Path.Combine(_tmp, "doc.pdf");
        File.WriteAllText(pdf, "stub");

        var chunks = Chunker.ChunkFile(pdf);
        Assert.NotEmpty(chunks);
        // Markdown language hint → MarkdownChunker → Heading locator.
        var heading = Assert.IsType<Locator.Heading>(chunks[0].Locator);
        Assert.Equal(new[] { "Heading" }, heading.Path);
    }

    [Fact]
    public void ChunkFile_Returns_Empty_When_Extractor_Throws()
    {
        var fake = new FakeExtractor
        {
            Throws = new InvalidOperationException("synthetic extractor failure"),
        };
        TextExtractors.Current = new TextExtractors(new Dictionary<string, IReadOnlyList<ITextExtractor>>
        {
            [".pdf"] = new ITextExtractor[] { fake },
        });

        var pdf = Path.Combine(_tmp, "doc.pdf");
        File.WriteAllText(pdf, "stub");

        Assert.Empty(Chunker.ChunkFile(pdf));
    }

    [Fact]
    public void ChunkFile_Falls_Back_To_File_Read_When_No_Extractor_Available()
    {
        TextExtractors.Current = new TextExtractors(new Dictionary<string, IReadOnlyList<ITextExtractor>>());

        var py = Path.Combine(_tmp, "x.py");
        File.WriteAllText(py, "x = 1\n");
        var chunks = Chunker.ChunkFile(py);
        Assert.NotEmpty(chunks);
        Assert.Equal("python", chunks[0].Language);
    }
}

/// <summary>
/// Availability-gated end-to-end test of the real <c>pdftotext</c> binary.
/// Skipped on hosts that don't have it on PATH so CI passes uniformly.
/// </summary>
public class PdftotextRealBinaryTests
{
    private static bool PdftotextAvailable() =>
        new Semble.Index.Extractors.PdftotextExtractor().IsAvailable;

    [SkippableFact]
    public void Real_Pdftotext_Extracts_Text_From_A_Tiny_Synthetic_Pdf()
    {
        Skip.IfNot(PdftotextAvailable(), "pdftotext is not on PATH on this host");
        // Write a minimal valid PDF (one page, one text run) so we don't need
        // to ship a binary fixture. The shape comes from the PDF 1.4 spec,
        // §7 (file structure) + §10 (showing text).
        var tmp = Path.Combine(Path.GetTempPath(), "semble-pdf-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            File.WriteAllBytes(tmp, MakeMinimalPdf("Hello PDF"));
            var extractor = new Semble.Index.Extractors.PdftotextExtractor();
            var result = extractor.ExtractText(tmp);
            Assert.Equal("text", result.Language);
            Assert.Contains("Hello PDF", result.Text, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    private static byte[] MakeMinimalPdf(string text)
    {
        // Hand-rolled PDF 1.4 with a single page rendering one text run in
        // Helvetica. Object offsets are computed at write time.
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var enc = System.Text.Encoding.ASCII;
        var offsets = new long[6];

        void WriteAscii(string s) => w.Write(enc.GetBytes(s));

        WriteAscii("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");
        offsets[1] = ms.Position;
        WriteAscii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = ms.Position;
        WriteAscii("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = ms.Position;
        WriteAscii(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
            "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        var content = $"BT /F1 12 Tf 50 100 Td ({text}) Tj ET";
        offsets[4] = ms.Position;
        WriteAscii($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        offsets[5] = ms.Position;
        WriteAscii("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        var xrefStart = ms.Position;
        WriteAscii("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++)
            WriteAscii($"{offsets[i]:0000000000} 00000 n \n");
        WriteAscii($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xrefStart}\n%%EOF\n");
        return ms.ToArray();
    }
}
