using System.Text.Json;

namespace Semble.Encoders;

/// <summary>
/// .NET equivalent of model2vec's <c>StaticModel</c> for the
/// <c>minishlab/potion-code-16M</c> family (and any other static-embedding
/// model produced by model2vec). The encode pipeline mirrors
/// <c>StaticModel._encode_batch</c> in src/semble's upstream tooling:
///
///   1. <see cref="HuggingFaceTokenizer.Encode"/> → token IDs (no special tokens, truncated to <see cref="MaxLength"/>)
///   2. (optional) remap IDs through <c>token_mapping</c> for vocabulary-quantised models
///   3. gather embeddings, optionally multiply each row by <c>weights[id]</c>
///   4. mean across rows
///   5. (optional) L2-normalise per the model's <c>config.normalize</c>
///
/// Result shape: <c>(texts.Count, dim)</c>, <c>float[,]</c>.
/// </summary>
/// <remarks>
/// The model's on-disk layout is the standard model2vec / sentence-transformers
/// folder produced by <c>StaticModel.save_pretrained</c>:
///
///   model-folder/
///     ├── tokenizer.json        (HuggingFace tokenizers JSON)
///     ├── model.safetensors     (with at minimum 'embeddings'; optionally 'weights' and 'mapping')
///     └── config.json           (with at least {"normalize": bool})
///
/// Models are downloaded separately (e.g. via <c>huggingface-cli download
/// minishlab/potion-code-16M</c>) and pointed at by <see cref="LoadFromDirectory"/>.
/// </remarks>
public sealed class PotionCodeEncoder : IEncoder
{
    public const int DefaultMaxLength = 512;

    private readonly HuggingFaceTokenizer _tokenizer;
    private readonly float[,] _embeddings;
    private readonly float[]? _weights;
    private readonly long[]? _tokenMapping;
    private readonly bool _normalize;
    private readonly int _maxLength;

    public int Dim => _embeddings.GetLength(1);
    public int VocabSize => _embeddings.GetLength(0);
    public int MaxLength => _maxLength;
    public bool Normalize => _normalize;

    public PotionCodeEncoder(
        HuggingFaceTokenizer tokenizer,
        float[,] embeddings,
        float[]? weights,
        long[]? tokenMapping,
        bool normalize,
        int maxLength = DefaultMaxLength)
    {
        _tokenizer = tokenizer;
        _embeddings = embeddings;
        _weights = weights;
        _tokenMapping = tokenMapping;
        _normalize = normalize;
        _maxLength = maxLength;

        // Sanity: with no token_mapping, vocab must match embedding rows.
        if (tokenMapping is null && tokenizer.VocabSize != embeddings.GetLength(0))
        {
            throw new InvalidDataException(
                $"vocab size ({tokenizer.VocabSize}) does not match embedding rows " +
                $"({embeddings.GetLength(0)}); a token_mapping is required for vocabulary-quantised models");
        }
    }

    /// <summary>
    /// Load a model from a directory containing <c>tokenizer.json</c>,
    /// <c>model.safetensors</c>, and <c>config.json</c>.
    /// </summary>
    public static PotionCodeEncoder LoadFromDirectory(string directory, int maxLength = DefaultMaxLength)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(
                $"Model directory not found: {directory}. Download a model2vec / potion model " +
                $"(e.g. `huggingface-cli download minishlab/potion-code-16M --local-dir <dir>`) " +
                $"and pass --model-path or set the SEMBLE_MODEL_PATH environment variable.");

        var tokenizerPath = Path.Combine(directory, "tokenizer.json");
        var modelPath = Path.Combine(directory, "model.safetensors");
        var configPath = Path.Combine(directory, "config.json");

        if (!File.Exists(tokenizerPath))
            throw new FileNotFoundException($"tokenizer.json not found in {directory}", tokenizerPath);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"model.safetensors not found in {directory}", modelPath);

        var tokenizer = HuggingFaceTokenizer.LoadFromJson(tokenizerPath);
        var safetensors = SafeTensors.File.Open(modelPath);

        // model2vec write 'embeddings'; sentence-transformers write 'embedding.weight'.
        var embeddings = safetensors.Tensors.ContainsKey("embeddings")
            ? safetensors.ReadFloat32Matrix("embeddings")
            : safetensors.ReadFloat32Matrix("embedding.weight");

        var weights = safetensors.ReadFloat32VectorOrNull("weights");
        var mapping = safetensors.ReadInt64VectorOrNull("mapping");

        bool normalize = false;
        if (File.Exists(configPath))
        {
            using var configStream = File.OpenRead(configPath);
            using var doc = JsonDocument.Parse(configStream);
            if (doc.RootElement.TryGetProperty("normalize", out var normProp)
                && normProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                normalize = normProp.GetBoolean();
            }
        }

        return new PotionCodeEncoder(tokenizer, embeddings, weights, mapping, normalize, maxLength);
    }

    public float[,] Encode(IReadOnlyList<string> texts)
    {
        int dim = Dim;
        var result = new float[texts.Count, dim];
        var sum = new double[dim]; // accumulate in double to avoid drift on long inputs

        for (int i = 0; i < texts.Count; i++)
        {
            Array.Clear(sum, 0, sum.Length);

            var ids = _tokenizer.Encode(texts[i], _maxLength);
            if (ids.Count == 0)
                continue; // row stays all-zeros, matching model2vec

            int matrixRows = _embeddings.GetLength(0);
            int counted = 0;
            foreach (var id in ids)
            {
                int row = id;
                if (_tokenMapping is not null)
                {
                    if (id < 0 || id >= _tokenMapping.Length)
                        continue;
                    long mapped = _tokenMapping[id];
                    if (mapped < 0 || mapped >= matrixRows)
                        continue;
                    row = (int)mapped;
                }
                if (row < 0 || row >= matrixRows)
                    continue;

                double weight = 1.0;
                if (_weights is not null && id >= 0 && id < _weights.Length)
                    weight = _weights[id];

                for (int d = 0; d < dim; d++)
                    sum[d] += _embeddings[row, d] * weight;
                counted++;
            }

            if (counted == 0)
                continue;

            double inv = 1.0 / counted;
            if (_normalize)
            {
                double sumSq = 0.0;
                for (int d = 0; d < dim; d++)
                {
                    double v = sum[d] * inv;
                    sum[d] = v;
                    sumSq += v * v;
                }
                double norm = Math.Sqrt(sumSq) + 1e-32;
                double normInv = 1.0 / norm;
                for (int d = 0; d < dim; d++)
                    result[i, d] = (float)(sum[d] * normInv);
            }
            else
            {
                for (int d = 0; d < dim; d++)
                    result[i, d] = (float)(sum[d] * inv);
            }
        }

        return result;
    }
}
