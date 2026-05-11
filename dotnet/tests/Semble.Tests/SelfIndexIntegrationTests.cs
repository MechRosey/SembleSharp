using Semble.Index;
using Xunit;
using static Semble.Tests.Fixtures;

namespace Semble.Tests;

/// <summary>
/// End-to-end integration test: index Semble's own source tree with the
/// deterministic <see cref="MockEncoder"/> and verify that BM25 searches for
/// known-good terms return the expected files. This catches glue mistakes
/// between the file walker, chunker, encoder seam, indexer, and search
/// pipeline that unit tests can miss.
/// </summary>
/// <remarks>
/// We use <c>MockEncoder</c> so this runs without a real ONNX/static model
/// downloaded to the host. Semantic and hybrid search would be noisy under a
/// mock encoder, so we only assert BM25-mode results. BM25 quality is a
/// pure function of the indexed corpus and tokenizer — no encoder involved.
/// </remarks>
public class SelfIndexIntegrationTests
{
    private static string SembleSourceDir
    {
        get
        {
            // Test bin layout: dotnet/tests/Semble.Tests/bin/<config>/<tfm>/Semble.Tests.dll
            // Semble source: dotnet/src/Semble/
            // → ../../../../../src/Semble from BaseDirectory
            var resolved = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Semble"));
            if (!Directory.Exists(resolved))
                throw new DirectoryNotFoundException(
                    $"Could not locate Semble source from test bin (looked at {resolved}). " +
                    $"Tests must be run from the repo's standard layout.");
            return resolved;
        }
    }

    private static SembleIndex BuildSelfIndex() =>
        SembleIndex.FromPath(SembleSourceDir, model: new MockEncoder());

    [Fact]
    public void Index_Of_Self_Has_Csharp_Chunks()
    {
        var idx = BuildSelfIndex();
        Assert.True(idx.Stats.IndexedFiles >= 20,
            $"expected to walk ≥20 .cs files; got {idx.Stats.IndexedFiles}");
        Assert.True(idx.Stats.TotalChunks > idx.Stats.IndexedFiles,
            "Roslyn chunker should produce multiple chunks per non-trivial .cs file");
        Assert.Contains("csharp", idx.Stats.Languages.Keys);
        Assert.True(idx.Stats.Languages["csharp"] > 50,
            $"expected ≥50 csharp chunks; got {idx.Stats.Languages.GetValueOrDefault("csharp")}");
    }

    [Theory]
    [InlineData("tokenize", "Tokens.cs")]              // bare verb → exact filename hit
    [InlineData("WordPiece", "HuggingFaceTokenizer.cs")] // distinctive symbol
    [InlineData("safetensors", "SafeTensors.cs")]       // distinctive symbol
    [InlineData("RerankTopK", "Penalties.cs")]          // function name
    [InlineData("ChunkSource", "Chunker.cs")]           // function name
    [InlineData("IsGitUrl", "Formatting.cs")]           // function name
    public void Bm25_Search_Surfaces_The_Right_File(string query, string expectedFile)
    {
        var idx = BuildSelfIndex();
        var results = idx.Search(query, topK: 10, mode: SearchMode.Bm25);
        Assert.NotEmpty(results);
        // The expected file should appear in the top results.
        var fileNames = results.Select(r => Path.GetFileName(r.Chunk.FilePath)).ToList();
        Assert.True(
            fileNames.Contains(expectedFile),
            $"query '{query}' should surface {expectedFile}; top results: [{string.Join(", ", fileNames)}]");
    }

    [Fact]
    public void Bm25_Top_Hit_For_Distinctive_Symbol_Is_The_Defining_File()
    {
        // Highly distinctive symbol — should be the #1 BM25 hit.
        var idx = BuildSelfIndex();
        var results = idx.Search("PotionCodeEncoder", topK: 5, mode: SearchMode.Bm25);
        Assert.NotEmpty(results);
        Assert.Equal("PotionCodeEncoder.cs", Path.GetFileName(results[0].Chunk.FilePath));
    }

    [Fact]
    public void Find_Related_From_A_Known_Chunk_Returns_Sensible_Neighbours()
    {
        var idx = BuildSelfIndex();
        // Find a chunk in Tokens.cs and look for related chunks.
        var tokensChunk = idx.Chunks.FirstOrDefault(c =>
            Path.GetFileName(c.FilePath) == "Tokens.cs");
        Assert.NotNull(tokensChunk);

        var related = idx.FindRelated(tokensChunk!, topK: 5);
        Assert.NotEmpty(related);
        Assert.All(related, r => Assert.NotEqual(tokensChunk, r.Chunk));
        // Note: with MockEncoder, semantic neighbours are essentially
        // hash-deterministic noise — we only assert that find_related
        // returns *something* and excludes the seed.
    }

    [Fact]
    public void Filter_Paths_Restricts_Bm25_Results_To_Specified_Files()
    {
        var idx = BuildSelfIndex();
        // Pick the first chunk's file path and constrain results to it.
        var targetPath = idx.Chunks[0].FilePath;
        var results = idx.Search("public", topK: 20, mode: SearchMode.Bm25,
            filterPaths: new[] { targetPath });
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal(targetPath, r.Chunk.FilePath));
    }

    [Fact]
    public void Empty_Query_Returns_Empty_Across_Modes()
    {
        var idx = BuildSelfIndex();
        Assert.Empty(idx.Search("", mode: SearchMode.Bm25));
        Assert.Empty(idx.Search("   ", mode: SearchMode.Hybrid));
        Assert.Empty(idx.Search("\n\n", mode: SearchMode.Semantic));
    }
}
