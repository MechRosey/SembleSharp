namespace Semble.Tests;

/// <summary>
/// Test helpers that mirror the Python tests/conftest.py fixtures and factories.
/// </summary>
internal static class Fixtures
{
    /// <summary>
    /// Mirrors tests/conftest.py:make_chunk — minimal Chunk with start_line=1 and
    /// end_line computed from newline count.
    /// </summary>
    public static Chunk MakeChunk(string content, string filePath = "src/module.py") =>
        new(
            Content: content,
            FilePath: filePath,
            StartLine: 1,
            EndLine: content.Count(c => c == '\n') + 1,
            Language: "python");

    /// <summary>
    /// Mirrors the mock_model fixture: a deterministic encoder that returns
    /// seeded unit-norm random vectors of the given dimension. Different from
    /// the Python numpy RNG bit-for-bit, but the structural properties tests
    /// rely on (shape, normalisation, determinism per-seed) hold the same way.
    /// </summary>
    public sealed class MockEncoder : IEncoder
    {
        private readonly int _dim;
        private readonly int _seed;

        public MockEncoder(int seed = 42, int dim = 256)
        {
            _seed = seed;
            _dim = dim;
        }

        public float[,] Encode(IReadOnlyList<string> texts)
        {
            // Deterministic per text: we seed the per-row RNG with a stable
            // hash of (encoder_seed, text) so identical content always maps to
            // the same embedding regardless of call order, and distinct content
            // maps to distinct embeddings.
            var m = new float[texts.Count, _dim];
            for (int i = 0; i < texts.Count; i++)
            {
                var rng = new Random(SeedFor(texts[i]));
                double sumSq = 0.0;
                for (int d = 0; d < _dim; d++)
                {
                    double u1 = 1.0 - rng.NextDouble();
                    double u2 = 1.0 - rng.NextDouble();
                    double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                    m[i, d] = (float)z;
                    sumSq += z * z;
                }
                double norm = Math.Sqrt(sumSq) + 1e-8;
                for (int d = 0; d < _dim; d++)
                    m[i, d] = (float)(m[i, d] / norm);
            }
            return m;
        }

        private int SeedFor(string text)
        {
            // FNV-1a 32-bit, mixed with the encoder seed. Stable across runs.
            unchecked
            {
                uint hash = 2166136261u;
                foreach (var ch in text)
                {
                    hash ^= ch;
                    hash *= 16777619u;
                }
                return (int)(hash ^ (uint)_seed);
            }
        }
    }
}
