using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Semble.Ranking;

/// <summary>Mirrors src/semble/ranking/boosting.py.</summary>
public static class Boosting
{
    // Symbol-lookup queries: namespace-qualified, leading-underscore, or containing
    // uppercase/underscore. Plain lowercase words ("session") are NL, not symbols.
    private static readonly Regex SymbolQueryRe = new(
        @"^(?:" +
        @"[A-Za-z_][A-Za-z0-9_]*(?:(?:::|\\|->|\.)[A-Za-z_][A-Za-z0-9_]*)+" + // namespace-qualified
        @"|_[A-Za-z0-9_]*" +                                                   // leading underscore
        @"|[A-Za-z][A-Za-z0-9]*[A-Z_][A-Za-z0-9_]*" +                          // contains uppercase or _
        @"|[A-Z][A-Za-z0-9]*" +                                                // starts with uppercase
        @")$",
        RegexOptions.Compiled);

    private static readonly Regex EmbeddedSymbolRe = new(
        @"\b(?:" +
        @"[A-Z][a-z][a-zA-Z0-9]*[A-Z][a-zA-Z0-9]*" + // PascalCase
        @"|[a-z][a-zA-Z0-9]*[A-Z][a-zA-Z0-9]+" +     // camelCase
        @")\b",
        RegexOptions.Compiled);

    private static readonly Regex KeywordExtractRe = new(
        @"[a-zA-Z_][a-zA-Z0-9_]*",
        RegexOptions.Compiled);

    private const int EmbeddedStemMinLen = 4;
    private const double EmbeddedSymbolBoostScale = 0.5;
    private const double DefinitionBoostMultiplier = 3.0;
    private const double StemBoostMultiplier = 1.0;
    private const double FileCoherenceBoostFrac = 0.2;

    private static readonly string[] DefinitionKeywords = new[]
    {
        "class", "module", "defmodule", "def", "interface", "struct", "enum",
        "trait", "type", "func", "function", "object", "abstract class",
        "data class", "fn", "fun", "package", "namespace", "protocol", "record",
        "typedef",
    };

    private static readonly string[] SqlDefinitionKeywords = new[]
    {
        "CREATE TABLE", "CREATE VIEW", "CREATE PROCEDURE", "CREATE FUNCTION",
    };

    private const string KeywordPrefix = @"(?:^|(?<=\s))(?:";

    private static readonly string DefinitionKeywordBody =
        string.Join("|", DefinitionKeywords.Select(Regex.Escape));

    private static readonly string SqlKeywordBody =
        string.Join("|", SqlDefinitionKeywords.Select(Regex.Escape));

    private static readonly HashSet<string> Stopwords = new(
        ("a an and are as at be by do does for from has have how if in is it not of on or the to" +
         " was what when where which who why with").Split(' '));

    private static readonly ConcurrentDictionary<string, (Regex General, Regex Sql)> DefinitionPatternCache = new();

    /// <summary>True if the query looks like a bare symbol or namespace-qualified identifier.</summary>
    public static bool IsSymbolQuery(string query) =>
        SymbolQueryRe.IsMatch(query.Trim());

    /// <summary>Apply query-type boosts to candidate scores. Returns a new dictionary.</summary>
    public static Dictionary<Chunk, double> ApplyQueryBoost(
        IReadOnlyDictionary<Chunk, double> combinedScores,
        string query,
        IReadOnlyList<Chunk> allChunks)
    {
        if (combinedScores.Count == 0)
            return new Dictionary<Chunk, double>();

        double maxScore = combinedScores.Values.Max();
        var boosted = new Dictionary<Chunk, double>(combinedScores);

        if (IsSymbolQuery(query))
        {
            BoostSymbolDefinitions(boosted, query, maxScore, allChunks);
        }
        else
        {
            BoostStemMatches(boosted, query, maxScore);
            BoostEmbeddedSymbols(boosted, query, maxScore, allChunks);
        }
        return boosted;
    }

    /// <summary>
    /// Promote files with multiple high-scoring chunks by boosting their top chunk
    /// (in-place mutation of <paramref name="scores"/>).
    /// </summary>
    public static void BoostMultiChunkFiles(Dictionary<Chunk, double> scores)
    {
        if (scores.Count == 0)
            return;

        double maxScore = scores.Values.Max();
        if (maxScore == 0.0)
            return;

        var fileSum = new Dictionary<string, double>();
        var bestChunk = new Dictionary<string, Chunk>();
        foreach (var (chunk, score) in scores)
        {
            var path = chunk.FilePath;
            fileSum[path] = (fileSum.TryGetValue(path, out var s) ? s : 0.0) + score;
            if (!bestChunk.TryGetValue(path, out var current) || score > scores[current])
            {
                bestChunk[path] = chunk;
            }
        }

        double maxFileSum = fileSum.Values.Max();
        double boostUnit = maxScore * FileCoherenceBoostFrac;
        foreach (var (path, chunk) in bestChunk)
        {
            scores[chunk] += boostUnit * fileSum[path] / maxFileSum;
        }
    }

    private static string ExtractSymbolName(string query)
    {
        foreach (var separator in new[] { "::", "\\", "->", "." })
        {
            int idx = query.LastIndexOf(separator, StringComparison.Ordinal);
            if (idx >= 0)
                return query[(idx + separator.Length)..];
        }
        return query.Trim();
    }

    private static (Regex General, Regex Sql) DefinitionPattern(string symbolName)
    {
        return DefinitionPatternCache.GetOrAdd(symbolName, name =>
        {
            var escaped = Regex.Escape(name);
            const string nsPrefix = @"(?:[A-Za-z_][A-Za-z0-9_]*(?:\.|::))*";
            var suffix = @")\s+" + nsPrefix + escaped + @"(?:\s|[<({:\[;]|$)";
            return (
                new Regex(KeywordPrefix + DefinitionKeywordBody + suffix,
                    RegexOptions.Compiled | RegexOptions.Multiline),
                new Regex(KeywordPrefix + SqlKeywordBody + suffix,
                    RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase));
        });
    }

    private static bool ChunkDefinesSymbol(Chunk chunk, string symbolName)
    {
        var (general, sql) = DefinitionPattern(symbolName);
        return general.IsMatch(chunk.Content) || sql.IsMatch(chunk.Content);
    }

    private static bool StemMatches(string stem, string name)
    {
        var stemNorm = stem.Replace("_", "");
        return stem == name
            || stemNorm == name
            || stem.TrimEnd('s') == name
            || stemNorm.TrimEnd('s') == name;
    }

    private static double DefinitionTier(Chunk chunk, IReadOnlyCollection<string> names, double boostUnit)
    {
        bool anyDefines = false;
        foreach (var n in names)
        {
            if (ChunkDefinesSymbol(chunk, n))
            {
                anyDefines = true;
                break;
            }
        }
        if (!anyDefines)
            return 0.0;
        var stem = PathHelpers.Stem(chunk.FilePath).ToLowerInvariant();
        bool stemMatches = false;
        foreach (var n in names)
        {
            if (StemMatches(stem, n.ToLowerInvariant()))
            {
                stemMatches = true;
                break;
            }
        }
        return boostUnit * (stemMatches ? 1.5 : 1.0);
    }

    private static void ScanNonCandidates(
        Dictionary<Chunk, double> boosted,
        IReadOnlyCollection<string> names,
        double boostUnit,
        IReadOnlyList<Chunk> allChunks,
        Func<string, bool> stemOk)
    {
        foreach (var chunk in allChunks)
        {
            if (boosted.ContainsKey(chunk))
                continue;
            var stem = PathHelpers.Stem(chunk.FilePath).ToLowerInvariant();
            if (!stemOk(stem))
                continue;
            var tier = DefinitionTier(chunk, names, boostUnit);
            if (tier > 0.0)
                boosted[chunk] = tier;
        }
    }

    private static void BoostSymbolDefinitions(
        Dictionary<Chunk, double> boosted,
        string query,
        double maxScore,
        IReadOnlyList<Chunk> allChunks)
    {
        var symbolName = ExtractSymbolName(query);
        var names = new HashSet<string>(StringComparer.Ordinal) { symbolName };
        var trimmed = query.Trim();
        if (symbolName != trimmed)
            names.Add(trimmed);

        double boostUnit = maxScore * DefinitionBoostMultiplier;

        foreach (var chunk in boosted.Keys.ToList())
        {
            var tier = DefinitionTier(chunk, names, boostUnit);
            if (tier > 0.0)
                boosted[chunk] += tier;
        }

        var symbolLower = symbolName.ToLowerInvariant();
        ScanNonCandidates(boosted, names, boostUnit, allChunks,
            stem => StemMatches(stem, symbolLower));
    }

    private static void BoostEmbeddedSymbols(
        Dictionary<Chunk, double> boosted,
        string query,
        double maxScore,
        IReadOnlyList<Chunk> allChunks)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in EmbeddedSymbolRe.Matches(query))
            names.Add(m.Value);
        if (names.Count == 0)
            return;

        double boostUnit = maxScore * DefinitionBoostMultiplier * EmbeddedSymbolBoostScale;

        foreach (var chunk in boosted.Keys.ToList())
        {
            var tier = DefinitionTier(chunk, names, boostUnit);
            if (tier > 0.0)
                boosted[chunk] += tier;
        }

        var symbolsLower = names.Select(n => n.ToLowerInvariant()).ToHashSet();
        foreach (var chunk in allChunks)
        {
            if (boosted.ContainsKey(chunk))
                continue;
            var stem = PathHelpers.Stem(chunk.FilePath).ToLowerInvariant();
            var stemNorm = stem.Replace("_", "");
            bool stemOk = false;
            foreach (var symbolLower in symbolsLower)
            {
                if (stem == symbolLower
                    || stemNorm == symbolLower
                    || (stem.Length >= EmbeddedStemMinLen && symbolLower.StartsWith(stem, StringComparison.Ordinal))
                    || (stemNorm.Length >= EmbeddedStemMinLen && symbolLower.StartsWith(stemNorm, StringComparison.Ordinal)))
                {
                    stemOk = true;
                    break;
                }
            }
            if (!stemOk)
                continue;
            var tier = DefinitionTier(chunk, names, boostUnit);
            if (tier > 0.0)
                boosted[chunk] = tier;
        }
    }

    private static int CountKeywordMatches(HashSet<string> keywords, HashSet<string> parts)
    {
        var exact = new HashSet<string>(keywords, StringComparer.Ordinal);
        exact.IntersectWith(parts);
        if (exact.Count == keywords.Count)
            return exact.Count;

        int n = exact.Count;
        foreach (var keyword in keywords)
        {
            if (exact.Contains(keyword))
                continue;
            foreach (var part in parts)
            {
                var (shorter, longer) = keyword.Length <= part.Length
                    ? (keyword, part)
                    : (part, keyword);
                if (shorter.Length >= 3 && longer.StartsWith(shorter, StringComparison.Ordinal))
                {
                    n++;
                    break;
                }
            }
        }
        return n;
    }

    private static void BoostStemMatches(
        Dictionary<Chunk, double> boosted,
        string query,
        double maxScore)
    {
        var keywords = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in KeywordExtractRe.Matches(query))
        {
            var word = m.Value;
            if (word.Length > 2)
            {
                var lower = word.ToLowerInvariant();
                if (!Stopwords.Contains(lower))
                    keywords.Add(lower);
            }
        }
        if (keywords.Count == 0)
            return;

        double boost = maxScore * StemBoostMultiplier;
        var pathCache = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var chunk in boosted.Keys.ToList())
        {
            if (!pathCache.TryGetValue(chunk.FilePath, out var parts))
            {
                parts = new HashSet<string>(Tokens.SplitIdentifier(PathHelpers.Stem(chunk.FilePath)),
                    StringComparer.Ordinal);
                var parentName = PathHelpers.ParentName(chunk.FilePath);
                if (parentName != "" && parentName != "." && parentName != "/" && parentName != "..")
                {
                    foreach (var p in Tokens.SplitIdentifier(parentName))
                        parts.Add(p);
                }
                pathCache[chunk.FilePath] = parts;
            }
            int nMatches = CountKeywordMatches(keywords, parts);
            if (nMatches > 0)
            {
                double matchRatio = (double)nMatches / keywords.Count;
                if (matchRatio >= 0.10)
                    boosted[chunk] += boost * matchRatio;
            }
        }
    }
}
