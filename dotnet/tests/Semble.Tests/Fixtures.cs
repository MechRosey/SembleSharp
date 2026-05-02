namespace Semble.Tests;

/// <summary>
/// Test helpers that mirror the Python tests/conftest.py fixtures and factories.
/// </summary>
internal static class Fixtures
{
    /// <summary>
    /// Mirrors tests/conftest.py:make_chunk — minimal Chunk with start_line=1 and
    /// end_line computed from newline count.
    /// </summary>
    public static Chunk MakeChunk(string content, string filePath = "src/module.py") =>
        new(
            Content: content,
            FilePath: filePath,
            StartLine: 1,
            EndLine: content.Count(c => c == '\n') + 1,
            Language: "python");
}
