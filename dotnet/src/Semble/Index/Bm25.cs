namespace Semble.Index;

/// <summary>
/// Hand-port of bm25s.BM25 with method='lucene' (the upstream default). Output
/// matches bm25s within float precision on the same token corpora.
/// </summary>
/// <remarks>
/// bm25s 'lucene' mode uses Lucene's IDF
/// <code>idf = ln(1 + (N - df + 0.5) / (df + 0.5))</code>
/// but drops the (k1+1) numerator from the standard Lucene tf normalisation
/// (verified empirically — see Bm25Tests). The simplification rescales every
/// score by the same constant, so document rankings are identical to standard
/// Lucene BM25.
/// </remarks>
public sealed class Bm25
{
    private readonly double _k1;
    private readonly double _b;

    private int _numDocs;
    private int[] _docLengths = Array.Empty<int>();
    private double _avgDocLength;
    private Dictionary<string, double> _idf = new();
    private Dictionary<string, Dictionary<int, int>> _postings = new();

    public Bm25(double k1 = 1.5, double b = 0.75)
    {
        _k1 = k1;
        _b = b;
    }

    public void Index(IReadOnlyList<IReadOnlyList<string>> corpus)
    {
        _numDocs = corpus.Count;
        _docLengths = new int[_numDocs];
        _postings = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);

        for (int d = 0; d < _numDocs; d++)
        {
            var doc = corpus[d];
            _docLengths[d] = doc.Count;
            foreach (var tok in doc)
            {
                if (!_postings.TryGetValue(tok, out var postings))
                {
                    postings = new Dictionary<int, int>();
                    _postings[tok] = postings;
                }
                postings[d] = postings.TryGetValue(d, out var n) ? n + 1 : 1;
            }
        }

        _avgDocLength = _numDocs == 0 ? 0.0 : _docLengths.Average();
        _idf = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (tok, postings) in _postings)
        {
            double df = postings.Count;
            _idf[tok] = Math.Log(1.0 + (_numDocs - df + 0.5) / (df + 0.5));
        }
    }

    /// <summary>
    /// Score every document against <paramref name="queryTokens"/>. Repeated query
    /// tokens contribute additively (matching bm25s).
    /// </summary>
    /// <param name="weightMask">Optional length-N boolean mask. Docs with mask=false
    /// receive zero score regardless of query match.</param>
    public double[] GetScores(IReadOnlyList<string> queryTokens, bool[]? weightMask = null)
    {
        var scores = new double[_numDocs];
        if (_numDocs == 0 || queryTokens.Count == 0 || _avgDocLength == 0.0)
            return scores;

        foreach (var tok in queryTokens)
        {
            if (!_idf.TryGetValue(tok, out var idf))
                continue;
            if (!_postings.TryGetValue(tok, out var postings))
                continue;
            foreach (var (d, tf) in postings)
            {
                if (weightMask is not null && !weightMask[d])
                    continue;
                double dl = _docLengths[d];
                double denom = tf + _k1 * (1.0 - _b + _b * dl / _avgDocLength);
                scores[d] += idf * tf / denom;
            }
        }
        return scores;
    }
}
