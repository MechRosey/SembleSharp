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
    /// <param name="modelPath">Explicit model directory; overrides env and cache.</param>
    /// <param name="autoDownload">
    /// When <c>true</c> and the resolved path does not exist, download the
    /// default model from HuggingFace Hub into the cache directory before
    /// loading.  Progress is written to <see cref="Console.Error"/>.
    /// </param>
    /// <exception cref="DirectoryNotFoundException">
    /// If no path resolves to an existing directory and
    /// <paramref name="autoDownload"/> is <c>false</c>. The message tells the
    /// user how to download the model from HuggingFace.
    /// </exception>
    public static IEncoder LoadModel(string? modelPath = null, bool autoDownload = false)
    {
        var resolved = TryResolveModelPath(modelPath, out var defaultDir);
        if (resolved is null)
        {
            if (!autoDownload)
                throw new DirectoryNotFoundException(
                    $"No Semble embedding model found. Pass --model-path, set {ModelPathEnvVar}, " +
                    $"or download the default model into '{defaultDir}', e.g.:\n" +
                    $"  huggingface-cli download {DefaultModelName} --local-dir '{defaultDir}'\n" +
                    $"Or run: semble download-model");

            Console.Error.WriteLine($"Downloading {DefaultModelName} to {defaultDir} ...");
            ModelDownloader.DownloadAsync(
                DefaultModelName,
                defaultDir!,
                (file, recv, total) =>
                {
                    string bar = total > 0
                        ? $" {recv * 100 / total,3}%"
                        : $" {recv / 1024 / 1024} MB";
                    Console.Error.Write($"\r  {file}{bar}    ");
                }).GetAwaiter().GetResult();
            Console.Error.WriteLine();
            resolved = defaultDir!;
        }
        return Encoders.PotionCodeEncoder.LoadFromDirectory(resolved);
    }

    /// <summary>
    /// Returns the resolved model path, or null if not found.
    /// <paramref name="defaultDir"/> is always set to the default cache path.
    /// </summary>
    private static string? TryResolveModelPath(string? explicitPath, out string defaultDir)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        defaultDir = System.IO.Path.Combine(home, ".cache", "semble", DefaultModelName);

        if (!string.IsNullOrEmpty(explicitPath))
            return explicitPath;

        var fromEnv = Environment.GetEnvironmentVariable(ModelPathEnvVar);
        if (!string.IsNullOrEmpty(fromEnv))
            return fromEnv;

        return Directory.Exists(defaultDir) ? defaultDir : null;
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
