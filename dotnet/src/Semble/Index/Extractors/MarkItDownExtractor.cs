namespace Semble.Index.Extractors;

/// <summary>
/// Extracts text from PDF / Office / many other formats via Microsoft's
/// <c>markitdown</c> CLI (https://github.com/microsoft/markitdown). Output
/// is markdown, so the post-extraction chunker dispatches through
/// <see cref="MarkdownChunker"/> and search results render with heading
/// breadcrumbs.
/// </summary>
/// <remarks>
/// The <c>markitdown</c> binary is a Python tool installed via
/// <c>pip install markitdown[all]</c>. When present on PATH it tends to give
/// noticeably better PDF extraction than <c>pdftotext</c> (column ordering,
/// list / heading recovery, table preservation), at the cost of a Python
/// runtime dependency.
/// </remarks>
internal sealed class MarkItDownExtractor : SubprocessExtractor
{
    public override string Name => "markitdown";
    protected override string ExecutableName => "markitdown";
    protected override string OutputLanguage => "markdown";

    protected override IReadOnlyList<string> BuildArguments(string filePath) => new[] { filePath };
}
