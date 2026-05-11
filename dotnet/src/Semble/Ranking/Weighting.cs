namespace Semble.Ranking;

/// <summary>Mirrors src/semble/ranking/weighting.py.</summary>
public static class Weighting
{
    private const double AlphaSymbol = 0.3; // lean BM25 for exact keyword matching
    private const double AlphaNl = 0.5;     // balanced semantic + BM25

    /// <summary>
    /// Return the blending weight for semantic scores, auto-detecting from query type.
    /// </summary>
    public static double ResolveAlpha(string query, double? alpha)
    {
        if (alpha.HasValue)
            return alpha.Value;
        return Boosting.IsSymbolQuery(query) ? AlphaSymbol : AlphaNl;
    }
}
