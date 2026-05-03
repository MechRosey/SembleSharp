using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Semble.Encoders;
using Xunit;

namespace Semble.Tests;

/// <summary>
/// End-to-end tests for <see cref="PotionCodeEncoder"/> using a synthetic
/// 6-token / 4-dim WordPiece model written to a temp directory in the
/// model2vec / sentence-transformers on-disk layout. We can't pull the real
/// minishlab/potion-code-16M from HuggingFace in CI, but the synthetic
/// fixture exercises every branch of the load + encode pipeline.
/// </summary>
public class PotionCodeEncoderTests : IDisposable
{
    private readonly string _tmp;

    public PotionCodeEncoderTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "semble-encoder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
    }

    private string WriteSyntheticModel(bool normalize, bool withWeights = false)
    {
        // 6-token vocab, 4-dim embeddings.
        var vocab = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["[PAD]"] = 0,
            ["[UNK]"] = 1,
            ["foo"] = 2,
            ["bar"] = 3,
            ["##s"] = 4,
            ["baz"] = 5,
        };
        var embeddings = new float[6, 4]
        {
            { 0.0f, 0.0f, 0.0f, 0.0f },   // PAD
            { 0.1f, 0.1f, 0.1f, 0.1f },   // UNK
            { 1.0f, 0.0f, 0.0f, 0.0f },   // foo
            { 0.0f, 1.0f, 0.0f, 0.0f },   // bar
            { 0.0f, 0.0f, 1.0f, 0.0f },   // ##s
            { 0.0f, 0.0f, 0.0f, 1.0f },   // baz
        };
        WriteTokenizerJson(vocab);
        WriteSafetensors(embeddings, withWeights ? new float[] { 1f, 1f, 1f, 1f, 1f, 1f } : null);
        WriteConfig(normalize);
        return _tmp;
    }

    private void WriteTokenizerJson(IReadOnlyDictionary<string, int> vocab)
    {
        var json = new
        {
            version = "1.0",
            normalizer = new
            {
                type = "BertNormalizer",
                clean_text = true,
                handle_chinese_chars = false,
                strip_accents = false,
                lowercase = true,
            },
            pre_tokenizer = new { type = "BertPreTokenizer" },
            model = new
            {
                type = "WordPiece",
                unk_token = "[UNK]",
                continuing_subword_prefix = "##",
                max_input_chars_per_word = 100,
                vocab = vocab,
            },
        };
        File.WriteAllText(
            Path.Combine(_tmp, "tokenizer.json"),
            JsonSerializer.Serialize(json));
    }

    private void WriteConfig(bool normalize)
    {
        var json = new { normalize = normalize };
        File.WriteAllText(
            Path.Combine(_tmp, "config.json"),
            JsonSerializer.Serialize(json));
    }

    private void WriteSafetensors(float[,] embeddings, float[]? weights)
    {
        int rows = embeddings.GetLength(0);
        int cols = embeddings.GetLength(1);
        long embBytes = (long)rows * cols * sizeof(float);
        long wBytes = weights is null ? 0 : (long)weights.Length * sizeof(float);

        // Header: { "embeddings": {...}, ["weights": {...}] }
        var sb = new StringBuilder();
        sb.Append("{\"embeddings\":{\"dtype\":\"F32\",\"shape\":[")
          .Append(rows).Append(',').Append(cols)
          .Append("],\"data_offsets\":[0,").Append(embBytes).Append("]}");
        if (weights is not null)
        {
            sb.Append(",\"weights\":{\"dtype\":\"F32\",\"shape\":[")
              .Append(weights.Length)
              .Append("],\"data_offsets\":[")
              .Append(embBytes).Append(',').Append(embBytes + wBytes).Append("]}");
        }
        sb.Append('}');

        var headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
        var path = Path.Combine(_tmp, "model.safetensors");
        using var stream = File.Create(path);
        Span<byte> lenBuf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(lenBuf, (ulong)headerBytes.Length);
        stream.Write(lenBuf);
        stream.Write(headerBytes);

        var rowBuf = new byte[cols * sizeof(float)];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
                BinaryPrimitives.WriteSingleLittleEndian(rowBuf.AsSpan(c * sizeof(float), sizeof(float)), embeddings[r, c]);
            stream.Write(rowBuf);
        }

        if (weights is not null)
        {
            var wBuf = new byte[sizeof(float)];
            foreach (var w in weights)
            {
                BinaryPrimitives.WriteSingleLittleEndian(wBuf, w);
                stream.Write(wBuf);
            }
        }
    }

    [Fact]
    public void LoadFromDirectory_Reads_All_Three_Artefacts()
    {
        var dir = WriteSyntheticModel(normalize: true);
        var enc = PotionCodeEncoder.LoadFromDirectory(dir);
        Assert.Equal(4, enc.Dim);
        Assert.Equal(6, enc.VocabSize);
        Assert.True(enc.Normalize);
    }

    [Fact]
    public void Encode_Empty_Text_Returns_Zero_Vector()
    {
        var dir = WriteSyntheticModel(normalize: false);
        var enc = PotionCodeEncoder.LoadFromDirectory(dir);
        var result = enc.Encode(new[] { "" });
        Assert.Equal(1, result.GetLength(0));
        Assert.Equal(4, result.GetLength(1));
        for (int d = 0; d < 4; d++)
            Assert.Equal(0f, result[0, d]);
    }

    [Fact]
    public void Encode_Single_Token_Returns_That_Tokens_Embedding_When_Not_Normalised()
    {
        var dir = WriteSyntheticModel(normalize: false);
        var enc = PotionCodeEncoder.LoadFromDirectory(dir);
        var result = enc.Encode(new[] { "foo" });
        Assert.Equal(1f, result[0, 0]);
        Assert.Equal(0f, result[0, 1]);
        Assert.Equal(0f, result[0, 2]);
        Assert.Equal(0f, result[0, 3]);
    }

    [Fact]
    public void Encode_Two_Tokens_Returns_Mean_When_Not_Normalised()
    {
        var dir = WriteSyntheticModel(normalize: false);
        var enc = PotionCodeEncoder.LoadFromDirectory(dir);
        // "foo bar" → mean( [1,0,0,0], [0,1,0,0] ) = [0.5, 0.5, 0, 0]
        var result = enc.Encode(new[] { "foo bar" });
        Assert.Equal(0.5f, result[0, 0]);
        Assert.Equal(0.5f, result[0, 1]);
        Assert.Equal(0f, result[0, 2]);
        Assert.Equal(0f, result[0, 3]);
    }

    [Fact]
    public void Encode_Continuation_Subword_Uses_Hash_Prefix_Vocab_Entry()
    {
        var dir = WriteSyntheticModel(normalize: false);
        var enc = PotionCodeEncoder.LoadFromDirectory(dir);
        // "foos" → ['foo', '##s'] → mean of [1,0,0,0] and [0,0,1,0] = [0.5,0,0.5,0]
        var result = enc.Encode(new[] { "foos" });
        Assert.Equal(0.5f, result[0, 0]);
        Assert.Equal(0f, result[0, 1]);
        Assert.Equal(0.5f, result[0, 2]);
        Assert.Equal(0f, result[0, 3]);
    }

    [Fact]
    public void Encode_Lowercases_When_Normalizer_Says_So()
    {
        var dir = WriteSyntheticModel(normalize: false);
        var enc = PotionCodeEncoder.LoadFromDirectory(dir);
        // "FOO" should lowercase → 'foo' embedding
        var resultUpper = enc.Encode(new[] { "FOO" });
        var resultLower = enc.Encode(new[] { "foo" });
        for (int d = 0; d < 4; d++)
            Assert.Equal(resultLower[0, d], resultUpper[0, d]);
    }

    [Fact]
    public void Encode_Unknown_Token_Falls_Back_To_Unk()
    {
        var dir = WriteSyntheticModel(normalize: false);
        var enc = PotionCodeEncoder.LoadFromDirectory(dir);
        // "qux" — no piece in vocab → emits [UNK] = embedding row 1 = [0.1,0.1,0.1,0.1]
        var result = enc.Encode(new[] { "qux" });
        for (int d = 0; d < 4; d++)
            Assert.Equal(0.1f, result[0, d], precision: 5);
    }

    [Fact]
    public void Encode_Normalises_To_Unit_Length_When_Configured()
    {
        var dir = WriteSyntheticModel(normalize: true);
        var enc = PotionCodeEncoder.LoadFromDirectory(dir);
        // "foo bar" → mean = [0.5,0.5,0,0], norm = sqrt(0.5) ≈ 0.7071,
        // normalised → [≈0.7071, ≈0.7071, 0, 0]
        var result = enc.Encode(new[] { "foo bar" });
        double sumSq = 0.0;
        for (int d = 0; d < 4; d++)
            sumSq += result[0, d] * (double)result[0, d];
        Assert.Equal(1.0, sumSq, precision: 5);
        Assert.Equal(0.70711f, result[0, 0], precision: 4);
        Assert.Equal(0.70711f, result[0, 1], precision: 4);
    }

    [Fact]
    public void Encode_Multiple_Texts_Returns_Matrix_With_One_Row_Each()
    {
        var dir = WriteSyntheticModel(normalize: false);
        var enc = PotionCodeEncoder.LoadFromDirectory(dir);
        var result = enc.Encode(new[] { "foo", "bar", "baz" });
        Assert.Equal(3, result.GetLength(0));
        Assert.Equal(4, result.GetLength(1));
        Assert.Equal(1f, result[0, 0]); // foo
        Assert.Equal(1f, result[1, 1]); // bar
        Assert.Equal(1f, result[2, 3]); // baz
    }

    [Fact]
    public void Encode_With_Per_Token_Weights_Scales_Each_Row_Before_Mean()
    {
        var dir = WriteSyntheticModel(normalize: false, withWeights: true);
        var enc = PotionCodeEncoder.LoadFromDirectory(dir);
        // weights are all 1.0, so behaves identically to no-weights.
        var result = enc.Encode(new[] { "foo bar" });
        Assert.Equal(0.5f, result[0, 0]);
        Assert.Equal(0.5f, result[0, 1]);
    }

    [Fact]
    public void HuggingFaceTokenizer_Bpe_Model_Type_Throws_NotSupported()
    {
        var path = Path.Combine(_tmp, "bpe.json");
        File.WriteAllText(path, """
            {
              "model": { "type": "BPE", "vocab": {}, "merges": [] }
            }
            """);
        Assert.Throws<NotSupportedException>(() => HuggingFaceTokenizer.LoadFromJson(path));
    }
}
