namespace Semble.Index;

/// <summary>
/// Mirrors src/semble/index/dense.py:SelectableBasicBackend — brute-force cosine
/// similarity over a 2D float32 matrix, with optional selector filtering.
/// Vectors are normalised to unit length on construction so a dot-product
/// against a normalised query yields the cosine similarity.
/// </summary>
public sealed class DenseBackend
{
    private readonly float[,] _vectors;
    private readonly int _numVectors;
    private readonly int _dim;

    public DenseBackend(float[,] vectors)
    {
        _vectors = NormaliseRows(vectors);
        _numVectors = _vectors.GetLength(0);
        _dim = _vectors.GetLength(1);
    }

    public int Count => _numVectors;
    public int Dimension => _dim;

    /// <summary>
    /// For each row in <paramref name="queries"/>, return the <paramref name="k"/>
    /// nearest neighbours by cosine distance (1 − cosine similarity), sorted
    /// ascending by distance. If <paramref name="selector"/> is supplied, only
    /// indices in the selector are considered and the returned indices map back
    /// to the underlying matrix.
    /// </summary>
    public (int[] Indices, float[] Distances)[] Query(
        float[,] queries,
        int k,
        int[]? selector = null)
    {
        if (k < 1)
            throw new ArgumentException($"k should be >= 1, is now {k}", nameof(k));

        int numQueries = queries.GetLength(0);
        if (numQueries == 0)
            return Array.Empty<(int[], float[])>();

        var queriesNorm = NormaliseRows(queries);

        int totalCandidates = selector?.Length ?? _numVectors;
        int effectiveK = Math.Min(k, totalCandidates);

        var results = new (int[], float[])[numQueries];
        for (int q = 0; q < numQueries; q++)
        {
            var distances = new float[totalCandidates];
            for (int j = 0; j < totalCandidates; j++)
            {
                int idx = selector?[j] ?? j;
                float sim = 0f;
                for (int d = 0; d < _dim; d++)
                    sim += queriesNorm[q, d] * _vectors[idx, d];
                distances[j] = 1f - sim;
            }

            // Stable top-k by ascending distance, ties broken by candidate index.
            var ordered = Enumerable.Range(0, totalCandidates)
                .Select(j => (LocalIndex: j, Dist: distances[j]))
                .OrderBy(t => t.Dist)
                .ThenBy(t => t.LocalIndex)
                .Take(effectiveK)
                .ToArray();

            var indices = new int[effectiveK];
            var dists = new float[effectiveK];
            for (int i = 0; i < effectiveK; i++)
            {
                indices[i] = selector is null ? ordered[i].LocalIndex : selector[ordered[i].LocalIndex];
                dists[i] = ordered[i].Dist;
            }
            results[q] = (indices, dists);
        }
        return results;
    }

    private static float[,] NormaliseRows(float[,] matrix)
    {
        int rows = matrix.GetLength(0);
        int cols = matrix.GetLength(1);
        var result = new float[rows, cols];
        for (int r = 0; r < rows; r++)
        {
            double sumSq = 0.0;
            for (int c = 0; c < cols; c++)
                sumSq += matrix[r, c] * (double)matrix[r, c];
            double norm = Math.Sqrt(sumSq);
            // Match Python: norm + 1e-8 to avoid divide-by-zero on all-zero rows.
            double inv = 1.0 / (norm + 1e-8);
            for (int c = 0; c < cols; c++)
                result[r, c] = (float)(matrix[r, c] * inv);
        }
        return result;
    }
}

/// <summary>
/// Embedding helpers mirroring src/semble/index/dense.py.
/// </summary>
public static class Dense
{
    public const string DefaultModelName = "minishlab/potion-code-16M";
    private const string ModelPathEnvVar = "SEMBLE_MODEL_PATH";

    /// <summary>
    /// Load a model2vec / potion static-embedding model from disk and return
    /// it as an <see cref="IEncoder"/>. Resolution order:
    ///   1. Explicit <paramref name="modelPath"/> argument
    ///   2. <c>SEMBLE_MODEL_PATH</c> environment variable
    ///   3. <c>~/.cache/semble/&lt;DefaultModelName&gt;/</c>
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">
    /// If no path resolves to an existing directory. The message tells the user
    /// how to download the model from HuggingFace.
    /// </exception>
    public static IEncoder LoadModel(string? modelPath = null)
    {
        var resolved = ResolveModelPath(modelPath);
        return Encoders.PotionCodeEncoder.LoadFromDirectory(resolved);
    }

    private static string ResolveModelPath(string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath))
            return explicitPath;

        var fromEnv = Environment.GetEnvironmentVariable(ModelPathEnvVar);
        if (!string.IsNullOrEmpty(fromEnv))
            return fromEnv;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultDir = System.IO.Path.Combine(home, ".cache", "semble", DefaultModelName);
        if (Directory.Exists(defaultDir))
            return defaultDir;

        var hfBase = $"https://huggingface.co/{DefaultModelName}";
        var hfApi  = $"https://huggingface.co/api/models/{DefaultModelName}";
        throw new DirectoryNotFoundException(
            $"No Semble embedding model found. Pass --model-path, set {ModelPathEnvVar}, " +
            $"or download the default model into '{defaultDir}'.\n\n" +
            $"Option A - huggingface-cli (if installed):\n" +
            $"  huggingface-cli download {DefaultModelName} --local-dir \"{defaultDir}\"\n\n" +
            $"Option B - direct download without extra tools:\n" +
            $"  Files are listed at: {hfApi}\n" +
            $"  Download each file from: {hfBase}/resolve/main/<filename>\n" +
            $"  Save to: {defaultDir}\n\n" +
            $"After downloading, restart your MCP client session for the change to take effect.");
    }

    /// <summary>Embed chunk content via the supplied encoder.</summary>
    public static float[,] EmbedChunks(IEncoder model, IReadOnlyList<Chunk> chunks)
    {
        if (chunks.Count == 0)
            return new float[0, 256];
        var texts = new string[chunks.Count];
        for (int i = 0; i < chunks.Count; i++)
            texts[i] = chunks[i].Content;
        return model.Encode(texts);
    }
}
