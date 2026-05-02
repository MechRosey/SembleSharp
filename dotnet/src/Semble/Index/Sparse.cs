using Semble.Ranking;

namespace Semble.Index;

/// <summary>Mirrors src/semble/index/sparse.py.</summary>
public static class Sparse
{
    /// <summary>Convert a selector array of indices into a boolean mask of length <paramref name="size"/>.</summary>
    public static bool[]? SelectorToMask(int[]? selector, int size)
    {
        if (selector is null)
            return null;
        var mask = new bool[size];
        foreach (var i in selector)
            mask[i] = true;
        return mask;
    }

    /// <summary>
    /// Append file-path components to BM25 content to boost path-based queries.
    /// Assumes <paramref name="chunk"/>.FilePath is repo-relative.
    /// </summary>
    public static string EnrichForBm25(Chunk chunk)
    {
        var path = chunk.FilePath.Replace('\\', '/');
        var stem = PathHelpers.Stem(path);

        // Directory parts: split on '/', drop the filename, drop empties and "."/"/".
        var segments = path.Split('/', StringSplitOptions.None);
        var dirParts = segments
            .Take(segments.Length - 1)
            .Where(p => p.Length > 0 && p != "." && p != "/")
            .ToList();
        var lastThree = dirParts.Skip(Math.Max(0, dirParts.Count - 3));
        var dirText = string.Join(" ", lastThree);

        return $"{chunk.Content} {stem} {stem} {dirText}";
    }
}
