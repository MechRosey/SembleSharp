using Semble.Index.Extractors;

namespace Semble.Index;

/// <summary>
/// Registry of <see cref="ITextExtractor"/> backends keyed by file extension,
/// with a priority order per extension. <see cref="For"/> returns the
/// highest-priority backend whose <see cref="ITextExtractor.IsAvailable"/>
/// is true, so the operator picks the active extractor by which binaries
/// are installed on PATH.
/// </summary>
/// <remarks>
/// Default priority for PDF and Office formats:
///   1. <c>markitdown</c>      — best output quality (heading recovery,
///                              table preservation), requires Python install
///   2. <c>pdftotext</c>       — Poppler binary, decent layout-preserving text,
///                              PDF only
///   3. <c>openxml-word</c>    — pure-managed .docx fallback via
///                              DocumentFormat.OpenXml, always available
/// All shell-out backends are gated on a host PATH probe; the openxml-word
/// fallback is always available so <c>.docx</c> is indexable on hosts with
/// no Python and no Poppler. When neither shell-out nor managed backend
/// matches the file is skipped at chunk time.
///
/// <see cref="Default"/> is the registry the production indexer reads from;
/// tests can construct a fresh instance with mocked extractors.
/// </remarks>
public sealed class TextExtractors
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ITextExtractor>> _byExtension;

    public TextExtractors(IReadOnlyDictionary<string, IReadOnlyList<ITextExtractor>> byExtension)
    {
        _byExtension = byExtension;
    }

    /// <summary>
    /// Return the highest-priority available extractor for
    /// <paramref name="filePath"/>'s extension, or null if no backend is
    /// registered or none is available.
    /// </summary>
    public ITextExtractor? For(string filePath)
    {
        var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
        if (!_byExtension.TryGetValue(ext, out var candidates))
            return null;
        foreach (var c in candidates)
        {
            if (c.IsAvailable)
                return c;
        }
        return null;
    }

    /// <summary>
    /// All extensions for which any backend is registered, regardless of
    /// availability. Used for testing the registry shape.
    /// </summary>
    public IReadOnlyCollection<string> RegisteredExtensions => _byExtension.Keys.ToArray();

    /// <summary>
    /// Default registry: shell-out backends in best-quality-first priority.
    /// Tests / callers can construct a custom registry and assign
    /// <see cref="Current"/> to swap it in.
    /// </summary>
    public static TextExtractors Default { get; } = BuildDefault();

    /// <summary>
    /// The registry the chunker actually consults. Tests swap this and reset
    /// in their <c>Dispose</c> to isolate from the host's installed binaries.
    /// </summary>
    public static TextExtractors Current { get; set; } = Default;

    private static TextExtractors BuildDefault()
    {
        var markitdown = new MarkItDownExtractor();
        var pdftotext = new PdftotextExtractor();
        var openxmlWord = new OpenXmlWordExtractor();

        // markitdown handles PDF + Office uniformly; pdftotext only PDF;
        // openxml-word is the pure-managed .docx fallback.
        var pdfBackends = new ITextExtractor[] { markitdown, pdftotext };
        var docxBackends = new ITextExtractor[] { markitdown, openxmlWord };
        var otherOfficeBackends = new ITextExtractor[] { markitdown };

        var map = new Dictionary<string, IReadOnlyList<ITextExtractor>>(StringComparer.Ordinal)
        {
            [".pdf"] = pdfBackends,
            [".docx"] = docxBackends,
            [".xlsx"] = otherOfficeBackends,
            [".pptx"] = otherOfficeBackends,
        };
        return new TextExtractors(map);
    }
}
