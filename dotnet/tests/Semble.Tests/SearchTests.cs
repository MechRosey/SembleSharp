using Semble.Index;
using Xunit;
using static Semble.Tests.Fixtures;

namespace Semble.Tests;

public class SearchFixtures
{
    public static List<Chunk> Chunks() => new()
    {
        MakeChunk("def authenticate(token):\n    return token == 'secret'", "auth.py"),
        MakeChunk("def login(username, password):\n    pass", "auth.py"),
        MakeChunk("class UserService:\n    pass", "users.py"),
        MakeChunk("def format_date(dt):\n    return str(dt)", "utils.py"),
    };

    public static Bm25 Bm25Over(IReadOnlyList<Chunk> chunks)
    {
        var b = new Bm25();
        b.Index(chunks.Select(c => (IReadOnlyList<string>)Tokens.Tokenize(c.Content)).ToList());
        return b;
    }

    public static DenseBackend SemanticOver(IReadOnlyList<Chunk> chunks)
    {
        var encoder = new MockEncoder(seed: 0);
        var embeddings = encoder.Encode(chunks.Select(c => c.Content).ToList());
        return new DenseBackend(embeddings);
    }
}

public class SearchBm25Tests
{
    [Fact]
    public void Returns_Most_Relevant_Chunk_First()
    {
        var chunks = SearchFixtures.Chunks();
        var bm25 = SearchFixtures.Bm25Over(chunks);
        var results = Search.SearchBm25("authenticate token", bm25, chunks, topK: 4, selector: null);
        Assert.NotEmpty(results);
        Assert.Contains("authenticate", results[0].Chunk.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Selector_Restricts_To_Given_Indices()
    {
        var chunks = SearchFixtures.Chunks();
        var bm25 = SearchFixtures.Bm25Over(chunks);
        var selector = new[] { chunks.Count - 1 };
        var filtered = Search.SearchBm25("format", bm25, chunks, topK: 4, selector: selector);
        Assert.All(filtered, r => Assert.Same(chunks[chunks.Count - 1], r.Chunk));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    [InlineData("zzzznonexistentterm")]
    public void Empty_Or_Unmatched_Query_Returns_Empty(string query)
    {
        var chunks = SearchFixtures.Chunks();
        var bm25 = SearchFixtures.Bm25Over(chunks);
        Assert.Empty(Search.SearchBm25(query, bm25, chunks, topK: 3, selector: null));
    }
}

public class SearchSemanticTests
{
    [Fact]
    public void Returns_Results_With_Scores_In_MinusOne_To_One()
    {
        var chunks = SearchFixtures.Chunks();
        var semantic = SearchFixtures.SemanticOver(chunks);
        var encoder = new MockEncoder();
        var results = Search.SearchSemantic("login", encoder, semantic, chunks, topK: 3, selector: null);
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.InRange(r.Score, -1.0, 1.0));
    }
}

public class SearchHybridTests
{
    [Fact]
    public void Returns_Combined_Results()
    {
        var chunks = SearchFixtures.Chunks();
        var semantic = SearchFixtures.SemanticOver(chunks);
        var bm25 = SearchFixtures.Bm25Over(chunks);
        var encoder = new MockEncoder();
        var results = Search.SearchHybrid("authenticate token", encoder, semantic, bm25, chunks, topK: 3);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void Identical_Content_In_Different_Files_Produces_Separate_Results()
    {
        const string sharedContent = "def helper():\n    pass";
        var chunkA = MakeChunk(sharedContent, "module_a.py");
        var chunkB = MakeChunk(sharedContent, "module_b.py");
        var allChunks = new List<Chunk> { chunkA, chunkB };

        var encoder = new MockEncoder(seed: 1);
        var embeddings = encoder.Encode(new[] { sharedContent, sharedContent });
        var semantic = new DenseBackend(embeddings);

        var bm25 = new Bm25();
        bm25.Index(allChunks.Select(c => (IReadOnlyList<string>)Tokens.Tokenize(c.Content)).ToList());

        var deduped = Search.SearchHybrid("helper", encoder, semantic, bm25, allChunks, topK: 5);
        var resultLocations = deduped.Select(r => r.Chunk.FilePath).ToHashSet();
        Assert.Contains("module_a.py", resultLocations);
        Assert.Contains("module_b.py", resultLocations);
    }
}

public class SearchSourceLabelTests
{
    [Fact]
    public void Bm25_Results_Carry_Bm25_Source()
    {
        var chunks = SearchFixtures.Chunks();
        var bm25 = SearchFixtures.Bm25Over(chunks);
        var results = Search.SearchBm25("authenticate", bm25, chunks, topK: 3, selector: null);
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal(SearchMode.Bm25, r.Source));
    }

    [Fact]
    public void Semantic_Results_Carry_Semantic_Source()
    {
        var chunks = SearchFixtures.Chunks();
        var semantic = SearchFixtures.SemanticOver(chunks);
        var encoder = new MockEncoder();
        var results = Search.SearchSemantic("query", encoder, semantic, chunks, topK: 4, selector: null);
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal(SearchMode.Semantic, r.Source));
    }

    [Fact]
    public void Hybrid_Results_Carry_Hybrid_Source()
    {
        var chunks = SearchFixtures.Chunks();
        var semantic = SearchFixtures.SemanticOver(chunks);
        var bm25 = SearchFixtures.Bm25Over(chunks);
        var encoder = new MockEncoder();
        var results = Search.SearchHybrid("login", encoder, semantic, bm25, chunks, topK: 4);
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal(SearchMode.Hybrid, r.Source));
    }
}

public class SortTopKTests
{
    [Fact]
    public void Equivalent_To_ArgsortMinusX()
    {
        var rng = new Random(0);
        var x = new double[10000];
        for (int i = 0; i < x.Length; i++)
            x[i] = rng.NextDouble() * 2 - 1;

        var topK = 100;
        var ours = Search.SortTopK(x, topK);

        var reference = Enumerable.Range(0, x.Length)
            .OrderByDescending(i => x[i])
            .ThenBy(i => i)
            .Take(topK)
            .ToArray();
        Assert.Equal(reference, ours);
    }
}

public class SelectableBackendGuardTests
{
    [Fact]
    public void Rejects_K_Below_One()
    {
        var encoder = new MockEncoder();
        var embeddings = encoder.Encode(new[] { "x" });
        var backend = new DenseBackend(embeddings);
        Assert.Throws<ArgumentException>(() => backend.Query(embeddings, k: 0));
    }
}
