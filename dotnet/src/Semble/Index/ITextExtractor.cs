namespace Semble.Index;

/// <summary>
/// Extracted text plus a hint about its post-extraction language. The
/// language flows back into <see cref="Chunker.ChunkSource"/> so a markdown
/// extractor (e.g. MarkItDown) can route its output through the
/// heading-aware chunker, while a plain-text extractor (e.g. <c>pdftotext</c>)
/// falls through to the line-based chunker.
/// </summary>
public sealed record ExtractedText(string Text, string Language);

/// <summary>
/// Pluggable text-extraction backend for binary document formats (PDF,
/// Office, etc.) that <see cref="File.ReadAllText(string)"/> can't decode
/// directly. Implementations must be safe to call concurrently from the
/// indexer's file walk.
/// </summary>
public interface ITextExtractor
{
    /// <summary>Quick check that the backend is usable on this host.
    /// Lookups, library loads, and binary-on-PATH probes go here so the
    /// hot path of <see cref="ExtractText"/> doesn't repeat them.</summary>
    bool IsAvailable { get; }

    /// <summary>Human-readable backend name, used for diagnostics and selection logs.</summary>
    string Name { get; }

    /// <summary>
    /// Extract the plain or markdown text content of <paramref name="filePath"/>.
    /// Implementations should throw on unrecoverable errors (file not found,
    /// permission denied, parse error); the caller treats those as
    /// "skip the file" and continues the walk.
    /// </summary>
    ExtractedText ExtractText(string filePath);
}
