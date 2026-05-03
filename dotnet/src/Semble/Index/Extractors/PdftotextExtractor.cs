namespace Semble.Index.Extractors;

/// <summary>
/// Extracts text from PDF via the <c>pdftotext</c> binary that ships with
/// Poppler. Layout-preserving (<c>-layout</c>) so columns and tables come out
/// in approximately reading order, with form-feed page separators kept so a
/// later step can split chunks by page if a <see cref="Locator.Pages"/>
/// coordinate is wanted.
/// </summary>
internal sealed class PdftotextExtractor : SubprocessExtractor
{
    public override string Name => "pdftotext";
    protected override string ExecutableName => "pdftotext";
    protected override string OutputLanguage => "text";

    protected override IReadOnlyList<string> BuildArguments(string filePath) => new[]
    {
        "-layout",
        "-enc", "UTF-8",
        filePath,
        "-",   // write to stdout
    };

    /// <summary>
    /// pdftotext separates pages with U+000C (form feed). Each form feed
    /// byte marks the boundary BETWEEN pages — the character following it is
    /// the first character of the next page. We emit a PageBreaks list of
    /// "first character offset of page N" entries, starting with 0 for page 1.
    /// </summary>
    protected override ExtractedText PostProcess(string stdout)
    {
        var breaks = new List<int> { 0 };
        for (int i = 0; i < stdout.Length; i++)
        {
            if (stdout[i] == '\f')
                breaks.Add(i + 1);
        }
        // Drop a trailing break if it points past the end (pdftotext often
        // emits a final \f at EOF; the resulting "page after the end" has
        // zero content and confuses downstream chunkers).
        if (breaks.Count > 1 && breaks[^1] >= stdout.Length)
            breaks.RemoveAt(breaks.Count - 1);
        return new ExtractedText(stdout, OutputLanguage, breaks);
    }
}
