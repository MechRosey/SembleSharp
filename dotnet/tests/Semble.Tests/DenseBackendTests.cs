using Semble.Index;
using Xunit;

namespace Semble.Tests;

public class DenseBackendTests
{
    private static float[,] FromRows(params float[][] rows)
    {
        int r = rows.Length, c = rows[0].Length;
        var m = new float[r, c];
        for (int i = 0; i < r; i++)
            for (int j = 0; j < c; j++)
                m[i, j] = rows[i][j];
        return m;
    }

    [Fact]
    public void Query_Returns_Identity_For_Same_Vectors()
    {
        var vectors = FromRows(
            new[] { 1f, 0f, 0f },
            new[] { 0f, 1f, 0f },
            new[] { 0f, 0f, 1f });
        var backend = new DenseBackend(vectors);

        var query = FromRows(new[] { 1f, 0f, 0f });
        var result = backend.Query(query, k: 3);

        Assert.Single(result);
        Assert.Equal(new[] { 0, 1, 2 }, result[0].Indices);
        // Distance to self is 0; orthogonal vectors are distance 1.
        Assert.Equal(0f, result[0].Distances[0], precision: 5);
        Assert.Equal(1f, result[0].Distances[1], precision: 5);
        Assert.Equal(1f, result[0].Distances[2], precision: 5);
    }

    [Fact]
    public void Query_Top_K_Returns_K_Nearest()
    {
        var vectors = FromRows(
            new[] { 1f, 0f },
            new[] { 0.9f, 0.1f },
            new[] { 0f, 1f });
        var backend = new DenseBackend(vectors);
        var query = FromRows(new[] { 1f, 0f });
        var result = backend.Query(query, k: 2);
        Assert.Equal(new[] { 0, 1 }, result[0].Indices);
        Assert.True(result[0].Distances[0] <= result[0].Distances[1]);
    }

    [Fact]
    public void Query_With_Selector_Restricts_Candidates_And_Maps_Back()
    {
        var vectors = FromRows(
            new[] { 1f, 0f },
            new[] { 0.9f, 0.1f },
            new[] { 0f, 1f });
        var backend = new DenseBackend(vectors);
        var query = FromRows(new[] { 1f, 0f });

        // Restrict to indices {1, 2} — index 0 (the perfect match) is excluded.
        var result = backend.Query(query, k: 2, selector: new[] { 1, 2 });
        Assert.Equal(new[] { 1, 2 }, result[0].Indices);
    }

    [Fact]
    public void Query_K_Greater_Than_Corpus_Returns_Effective_K()
    {
        var vectors = FromRows(new[] { 1f, 0f }, new[] { 0f, 1f });
        var backend = new DenseBackend(vectors);
        var query = FromRows(new[] { 1f, 0f });
        var result = backend.Query(query, k: 99);
        Assert.Equal(2, result[0].Indices.Length);
    }

    [Fact]
    public void Query_K_Less_Than_One_Throws()
    {
        var backend = new DenseBackend(FromRows(new[] { 1f, 0f }));
        Assert.Throws<ArgumentException>(() =>
            backend.Query(FromRows(new[] { 1f, 0f }), k: 0));
    }

    [Fact]
    public void Empty_Queries_Return_Empty_Result_Array()
    {
        var backend = new DenseBackend(FromRows(new[] { 1f, 0f }));
        var result = backend.Query(new float[0, 2], k: 1);
        Assert.Empty(result);
    }

    [Fact]
    public void Constructor_Normalises_Stored_Vectors()
    {
        // A non-unit-norm vector and a unit vector should compare like-for-like.
        var vectors = FromRows(new[] { 3f, 0f }, new[] { 0f, 5f });
        var backend = new DenseBackend(vectors);
        var result = backend.Query(FromRows(new[] { 1f, 0f }), k: 2);
        Assert.Equal(0, result[0].Indices[0]);
        Assert.Equal(0f, result[0].Distances[0], precision: 5);
        Assert.Equal(1, result[0].Indices[1]);
        Assert.Equal(1f, result[0].Distances[1], precision: 5);
    }
}

public class DenseEmbedChunksTests
{
    private sealed class IdentityEncoder : IEncoder
    {
        private readonly int _dim;
        public IdentityEncoder(int dim) { _dim = dim; }
        public float[,] Encode(IReadOnlyList<string> texts)
        {
            var m = new float[texts.Count, _dim];
            for (int i = 0; i < texts.Count; i++)
                m[i, 0] = texts[i].Length;
            return m;
        }
    }

    [Fact]
    public void EmbedChunks_Empty_Returns_Empty_Matrix()
    {
        var enc = new IdentityEncoder(256);
        var result = Dense.EmbedChunks(enc, Array.Empty<Chunk>());
        Assert.Equal(0, result.GetLength(0));
        Assert.Equal(256, result.GetLength(1));
    }

    [Fact]
    public void EmbedChunks_Forwards_Content_To_Encoder()
    {
        var enc = new IdentityEncoder(8);
        var chunks = new[]
        {
            new Chunk("hello", "a.py", 1, 1),
            new Chunk("world!!", "b.py", 1, 1),
        };
        var result = Dense.EmbedChunks(enc, chunks);
        Assert.Equal(2, result.GetLength(0));
        Assert.Equal(8, result.GetLength(1));
        Assert.Equal(5f, result[0, 0]);
        Assert.Equal(7f, result[1, 0]);
    }

    [Fact]
    public void LoadModel_With_Missing_Path_Throws_DirectoryNotFound_With_Helpful_Message()
    {
        var bogus = Path.Combine(Path.GetTempPath(), "semble-no-such-model-" + Guid.NewGuid().ToString("N"));
        var ex = Assert.Throws<DirectoryNotFoundException>(() => Dense.LoadModel(bogus));
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
