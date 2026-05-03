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
}
