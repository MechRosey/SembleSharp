using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Semble;

/// <summary>Mirrors src/semble/utils.py.</summary>
public static class Formatting
{
    private static readonly string[] GitUrlSchemes =
        { "https://", "http://", "ssh://", "git://", "git+ssh://", "file://" };

    private static readonly Regex ScpGitUrlRe = new(
        @"^[\w.-]+@[\w.-]+:(?!/)",
        RegexOptions.Compiled);

    /// <summary>True if <paramref name="path"/> looks like a remote git URL rather than a local path.</summary>
    public static bool IsGitUrl(string path)
    {
        foreach (var scheme in GitUrlSchemes)
        {
            if (path.StartsWith(scheme, StringComparison.Ordinal))
                return true;
        }
        return ScpGitUrlRe.IsMatch(path);
    }

    /// <summary>
    /// Return the chunk containing <paramref name="line"/> in <paramref name="filePath"/>,
    /// or null. Boundary chunks (where line == end_line) are kept as a fallback in case
    /// no inner chunk matches — the same heuristic as the Python implementation.
    /// </summary>
    public static Chunk? ResolveChunk(IReadOnlyList<Chunk> chunks, string filePath, int line)
    {
        Chunk? fallback = null;
        foreach (var chunk in chunks)
        {
            if (chunk.FilePath == filePath && chunk.StartLine <= line && line <= chunk.EndLine)
            {
                if (line < chunk.EndLine)
                    return chunk;
                if (fallback is null)
                    fallback = chunk;
            }
        }
        return fallback;
    }

    /// <summary>Render search results as numbered, fenced code blocks.</summary>
    public static string FormatResults(string header, IReadOnlyList<SearchResult> results)
    {
        var sb = new StringBuilder();
        sb.Append(header).Append('\n').Append('\n');
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            sb.Append("## ")
              .Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(". ")
              .Append(r.Chunk.Location)
              .Append("  [score=")
              .Append(r.Score.ToString("F3", CultureInfo.InvariantCulture))
              .Append(']').Append('\n');
            sb.Append("```").Append('\n');
            sb.Append(r.Chunk.Content.Trim()).Append('\n');
            sb.Append("```").Append('\n').Append('\n');
        }
        // Python's "\n".join() leaves no trailing newline; the manual lines.append("")
        // followed by join leaves exactly one. Match that.
        if (sb.Length > 0 && sb[^1] == '\n')
            sb.Length--;
        return sb.ToString();
    }
}
