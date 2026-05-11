using Semble.Index;
using Semble.Ranking;

namespace Semble;

/// <summary>Mirrors src/semble/search.py.</summary>
public static class Search
{
    private const int RrfK = 60;

    public static List<SearchResult> SearchSemantic(
        string query,
        IEncoder model,
        DenseBackend semanticIndex,
        IReadOnlyList<Chunk> chunks,
        int topK,
        int[]? selector)
    {
        var queryEmbedding = model.Encode(new[] { query });
        var batch = semanticIndex.Query(queryEmbedding, k: topK, selector: selector);
        if (batch.Length == 0)
            return new List<SearchResult>();
        var (indices, distances) = batch[0];
        var results = new List<SearchResult>(indices.Length);
        for (int i = 0; i < indices.Length; i++)
        {
            results.Add(new SearchResult(chunks[indices[i]], 1.0 - distances[i], SearchMode.Semantic));
        }
        return results;
    }

    /// <summary>
    /// Return the indices of the top-<paramref name="topK"/> values in
    /// <paramref name="arr"/> sorted descending. Mirrors the upstream
    /// _sort_top_k helper (np.argsort(-x)[:top_k] for the no-tie case).
    /// </summary>
    public static int[] SortTopK(double[] arr, int topK)
    {
        int n = arr.Length;
        var indices = Enumerable.Range(0, n).ToArray();
        Array.Sort(indices, (a, b) =>
        {
            int cmp = arr[b].CompareTo(arr[a]);
            return cmp != 0 ? cmp : a.CompareTo(b);
        });
        int k = Math.Min(topK, n);
        return indices.Take(k).ToArray();
    }

    public static List<SearchResult> SearchBm25(
        string query,
        Bm25 bm25Index,
        IReadOnlyList<Chunk> chunks,
        int topK,
        int[]? selector)
    {
        var tokens = Tokens.Tokenize(query);
        if (tokens.Count == 0)
            return new List<SearchResult>();
        var mask = Sparse.SelectorToMask(selector, chunks.Count);
        var scores = bm25Index.GetScores(tokens, mask);
        var indices = SortTopK(scores, topK);
        var results = new List<SearchResult>();
        foreach (var i in indices)
        {
            if (scores[i] > 0)
                results.Add(new SearchResult(chunks[i], scores[i], SearchMode.Bm25));
        }
        return results;
    }

    public static List<SearchResult> SearchHybrid(
        string query,
        IEncoder model,
        DenseBackend semanticIndex,
        Bm25 bm25Index,
        IReadOnlyList<Chunk> chunks,
        int topK,
        double? alpha = null,
        int[]? selector = null)
    {
        double alphaWeight = Weighting.ResolveAlpha(query, alpha);
        int candidateCount = topK * 5;

        var semantic = SearchSemantic(query, model, semanticIndex, chunks, candidateCount, selector);
        var semanticScores = new Dictionary<Chunk, double>();
        foreach (var r in semantic)
            semanticScores[r.Chunk] = r.Score;

        var bm25Scores = new Dictionary<Chunk, double>();
        foreach (var r in SearchBm25(query, bm25Index, chunks, candidateCount, selector))
        {
            if (r.Score != 0)
                bm25Scores[r.Chunk] = r.Score;
        }

        var normSemantic = RrfScores(semanticScores);
        var normBm25 = RrfScores(bm25Scores);

        var combined = new Dictionary<Chunk, double>();
        var union = new HashSet<Chunk>(normSemantic.Keys);
        union.UnionWith(normBm25.Keys);
        foreach (var c in union)
        {
            double s = 0.0;
            if (normSemantic.TryGetValue(c, out var ss))
                s += alphaWeight * ss;
            if (normBm25.TryGetValue(c, out var bs))
                s += (1.0 - alphaWeight) * bs;
            combined[c] = s;
        }

        Boosting.BoostMultiChunkFiles(combined);
        var boosted = Boosting.ApplyQueryBoost(combined, query, chunks);
        var ranked = Penalties.RerankTopK(boosted, topK, penalisePaths: alphaWeight < 1.0);
        return ranked
            .Select(t => new SearchResult(t.Chunk, t.Score, SearchMode.Hybrid))
            .ToList();
    }

    private static Dictionary<Chunk, double> RrfScores(IReadOnlyDictionary<Chunk, double> scores)
    {
        var result = new Dictionary<Chunk, double>();
        if (scores.Count == 0)
            return result;
        var ranked = scores.Keys
            .Select((c, i) => (Chunk: c, Score: scores[c], Index: i))
            .OrderByDescending(t => t.Score)
            .ThenBy(t => t.Index)
            .ToList();
        for (int rank = 0; rank < ranked.Count; rank++)
        {
            result[ranked[rank].Chunk] = 1.0 / (RrfK + (rank + 1));
        }
        return result;
    }
}
