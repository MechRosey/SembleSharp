using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Semble.Index.Extractors;

/// <summary>
/// Pure-managed <c>.docx</c> extractor that walks the WordprocessingML
/// document tree and renders it to markdown so search results inherit
/// heading paths via <see cref="MarkdownChunker"/> + <see cref="Locator.Heading"/>.
/// Backed by Microsoft's first-party <c>DocumentFormat.OpenXml</c> NuGet.
/// </summary>
/// <remarks>
/// <para>
/// Heading detection looks at each paragraph's <c>w:pStyle</c>:
/// <c>Heading1</c>..<c>Heading6</c> map to <c>#</c>..<c>######</c>;
/// <c>Title</c> maps to <c>#</c>; <c>Subtitle</c> to <c>##</c>. Other paragraph
/// styles render as plain paragraphs.
/// </para>
/// <para>
/// Tables are flattened: each cell's paragraphs render in row-major order
/// without markdown table syntax. Indexing/search doesn't need column
/// alignment, and the alternative (rendering proper markdown tables) tends
/// to look terrible in search snippets when columns are wide. Comments,
/// footnotes, and tracked changes are ignored.
/// </para>
/// </remarks>
internal sealed class OpenXmlWordExtractor : ITextExtractor
{
    public string Name => "openxml-word";

    /// <summary>Always available — managed library, no external deps.</summary>
    public bool IsAvailable => true;

    public ExtractedText ExtractText(string filePath)
    {
        try
        {
            using var doc = WordprocessingDocument.Open(filePath, isEditable: false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body is null)
                return new ExtractedText("", "markdown");

            var sb = new StringBuilder();
            EmitChildren(body, sb);
            return new ExtractedText(sb.ToString(), "markdown");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Wrap parser failures in InvalidOperationException so
            // Chunker.ChunkFile's catch list treats this like any other
            // extractor failure (skip the file, don't abort the walk).
            throw new InvalidOperationException(
                $"openxml-word: failed to extract '{filePath}': {ex.Message}", ex);
        }
    }

    private static void EmitChildren(OpenXmlElement parent, StringBuilder sb)
    {
        foreach (var child in parent.ChildElements)
        {
            switch (child)
            {
                case Paragraph p:
                    EmitParagraph(p, sb);
                    break;
                case Table t:
                    EmitTable(t, sb);
                    break;
                // SectionProperties, BookmarkStart/End, ProofErr, etc. — ignore.
            }
        }
    }

    private static void EmitParagraph(Paragraph p, StringBuilder sb)
    {
        var styleId = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        var headingLevel = TryGetHeadingLevel(styleId);

        var text = ExtractParagraphText(p);
        if (text.Length == 0 && !headingLevel.HasValue)
            return;

        if (headingLevel.HasValue)
        {
            sb.Append(new string('#', headingLevel.Value));
            sb.Append(' ');
            sb.Append(text);
            sb.Append('\n').Append('\n');
        }
        else
        {
            sb.Append(text).Append('\n').Append('\n');
        }
    }

    private static void EmitTable(Table t, StringBuilder sb)
    {
        foreach (var row in t.Elements<TableRow>())
        {
            foreach (var cell in row.Elements<TableCell>())
            {
                foreach (var para in cell.Elements<Paragraph>())
                {
                    // Treat cell paragraphs as plain paragraphs even if they
                    // carry a heading style — heading hierarchies inside
                    // tables tend to be presentational, not structural.
                    var text = ExtractParagraphText(para);
                    if (text.Length > 0)
                        sb.Append(text).Append('\n');
                }
            }
            sb.Append('\n');
        }
    }

    private static string ExtractParagraphText(Paragraph p)
    {
        var sb = new StringBuilder();
        foreach (var node in p.Descendants())
        {
            switch (node)
            {
                case Text t:
                    sb.Append(t.Text);
                    break;
                case TabChar:
                    sb.Append('\t');
                    break;
                case Break br:
                    // A <w:br/> with type="page" or "column" is a page/column
                    // break; the others are line breaks. For indexing we
                    // collapse all of them to a space so a heading isn't
                    // split into two markdown headings.
                    sb.Append(br.Type?.Value == BreakValues.TextWrapping || br.Type is null
                        ? '\n'
                        : ' ');
                    break;
                case CarriageReturn:
                    sb.Append('\n');
                    break;
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static int? TryGetHeadingLevel(string? styleId)
    {
        if (string.IsNullOrEmpty(styleId))
            return null;
        if (styleId == "Title")
            return 1;
        if (styleId == "Subtitle")
            return 2;
        // Word's default heading style IDs are "Heading1".."Heading9"; we
        // cap at 6 since that's the markdown maximum.
        if (styleId.StartsWith("Heading", StringComparison.Ordinal)
            && int.TryParse(styleId.AsSpan(7), out var level)
            && level >= 1 && level <= 6)
            return level;
        return null;
    }
}
