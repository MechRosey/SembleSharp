using System.Text.RegularExpressions;

namespace Semble.Ranking;

/// <summary>Mirrors src/semble/ranking/penalties.py.</summary>
public static class Penalties
{
    private static readonly Regex TestFileRe = new(
        @"(?:^|/)" +
        @"(?:" +
        // Python
        @"test_[^/]*\.py" +
        @"|[^/]*_test\.py" +
        // Go
        @"|[^/]*_test\.go" +
        // Java
        @"|[^/]*Tests?\.java" +
        // PHP
        @"|[^/]*Test\.php" +
        // Ruby
        @"|[^/]*_spec\.rb" +
        @"|[^/]*_test\.rb" +
        // JavaScript / TypeScript
        @"|[^/]*\.test\.[jt]sx?" +
        @"|[^/]*\.spec\.[jt]sx?" +
        // Kotlin
        @"|[^/]*Tests?\.kt" +
        @"|[^/]*Spec\.kt" +
        // Swift
        @"|[^/]*Tests?\.swift" +
        @"|[^/]*Spec\.swift" +
        // C#
        @"|[^/]*Tests?\.cs" +
        // C / C++
        @"|test_[^/]*\.cpp" +
        @"|[^/]*_test\.cpp" +
        @"|test_[^/]*\.c" +
        @"|[^/]*_test\.c" +
        // Scala
        @"|[^/]*Spec\.scala" +
        @"|[^/]*Suite\.scala" +
        @"|[^/]*Test\.scala" +
        // Dart
        @"|[^/]*_test\.dart" +
        @"|test_[^/]*\.dart" +
        // Lua
        @"|[^/]*_spec\.lua" +
        @"|[^/]*_test\.lua" +
        @"|test_[^/]*\.lua" +
        // Shared helper patterns (all languages)
        @"|test_helpers?[^/]*\.\w+" +
        @")$",
        RegexOptions.Compiled);

    private static readonly Regex TestDirRe = new(
        @"(?:^|/)(?:tests?|__tests__|spec|testing)(?:/|$)",
        RegexOptions.Compiled);

    private static readonly Regex CompatDirRe = new(
        @"(?:^|/)(?:compat|_compat|legacy)(?:/|$)",
        RegexOptions.Compiled);

    private static readonly Regex ExamplesDirRe = new(
        @"(?:^|/)(?:_?examples?|docs?_src)(?:/|$)",
        RegexOptions.Compiled);

    private static readonly Regex TypeDefsRe = new(
        @"\.d\.ts$",
        RegexOptions.Compiled);

    private const double StrongPenalty = 0.3;     // test files, compat shims, example/doc code
    private const double ModeratePenalty = 0.5;   // re-export / metadata files
    private const double MildPenalty = 0.7;       // .d.ts declaration stubs

    private static readonly HashSet<string> ReexportFilenames = new(StringComparer.Ordinal)
    {
        "__init__.py",
        "package-info.java",
    };

    private const int FileSaturationThreshold = 1;
    private const double FileSaturationDecay = 0.5;

    /// <summary>
    /// Select top-k results with optional file-path penalties and file-saturation decay.
    /// </summary>
    public static List<(Chunk Chunk, double Score)> RerankTopK(
        IReadOnlyDictionary<Chunk, double> scores,
        int topK,
        bool penalisePaths = true)
    {
        if (scores.Count == 0)
            return new List<(Chunk, double)>();

        // Apply file-path penalties.
        var penaltyCache = new Dictionary<string, double>(StringComparer.Ordinal);
        var penalised = new Dictionary<Chunk, double>();
        foreach (var (chunk, score) in scores)
        {
            if (penalisePaths)
            {
                if (!penaltyCache.TryGetValue(chunk.FilePath, out var pen))
                {
                    pen = FilePathPenalty(chunk.FilePath);
                    penaltyCache[chunk.FilePath] = pen;
                }
                penalised[chunk] = score * pen;
            }
            else
            {
                penalised[chunk] = score;
            }
        }

        // Stable sort by penalised score, highest first.
        var ranked = penalised.Keys
            .Select((c, i) => (Chunk: c, Score: penalised[c], Index: i))
            .OrderByDescending(t => t.Score)
            .ThenBy(t => t.Index)
            .Select(t => t.Chunk)
            .ToList();

        var fileSelected = new Dictionary<string, int>(StringComparer.Ordinal);
        var selected = new List<(double Score, Chunk Chunk)>();
        double minSelected = double.PositiveInfinity;

        foreach (var chunk in ranked)
        {
            double penScore = penalised[chunk];
            if (selected.Count >= topK && penScore <= minSelected)
                break;

            int alreadySelected = fileSelected.TryGetValue(chunk.FilePath, out var n) ? n : 0;
            double effScore = penScore;
            if (alreadySelected >= FileSaturationThreshold)
            {
                int excess = alreadySelected - FileSaturationThreshold + 1;
                effScore *= Math.Pow(FileSaturationDecay, excess);
            }

            selected.Add((effScore, chunk));
            fileSelected[chunk.FilePath] = alreadySelected + 1;

            if (selected.Count >= topK)
            {
                minSelected = selected.Min(t => t.Score);
            }
        }

        // Sort selected by effective score, descending; stable on insertion order.
        var orderedSelected = selected
            .Select((t, i) => (t.Score, t.Chunk, Index: i))
            .OrderByDescending(t => t.Score)
            .ThenBy(t => t.Index)
            .Take(topK)
            .Select(t => (t.Chunk, t.Score))
            .ToList();
        return orderedSelected;
    }

    private static double FilePathPenalty(string filePath)
    {
        var normalised = filePath.Replace('\\', '/');
        double penalty = 1.0;
        if (TestFileRe.IsMatch(normalised) || TestDirRe.IsMatch(normalised))
            penalty *= StrongPenalty;
        if (ReexportFilenames.Contains(PathHelpers.Name(filePath)))
            penalty *= ModeratePenalty;
        if (CompatDirRe.IsMatch(normalised))
            penalty *= StrongPenalty;
        if (ExamplesDirRe.IsMatch(normalised))
            penalty *= StrongPenalty;
        if (TypeDefsRe.IsMatch(normalised))
            penalty *= MildPenalty;
        return penalty;
    }
}
