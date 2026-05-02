using Semble.Index;
using Xunit;
using static Semble.Tests.Fixtures;

namespace Semble.Tests;

public class Bm25Tests
{
    // The reference scores below were captured from upstream `bm25s.BM25` with
    // method='lucene' (the default) on the same token corpus.
    private static readonly List<List<string>> ReferenceCorpus = new()
    {
        new() { "def", "authenticate", "token" },
        new() { "def", "login", "user" },
        new() { "class", "config", "host" },
        new() { "def", "authenticate", "session", "token" },
    };

    private static Bm25 IndexedReference()
    {
        var b = new Bm25();
        b.Index(ReferenceCorpus);
        return b;
    }

    [Fact]
    public void Single_Term_Query_Matches_Bm25s_Lucene()
    {
        var b = IndexedReference();
        var scores = b.GetScores(new[] { "authenticate" });
        Assert.Equal(0.287200, scores[0], precision: 6);
        Assert.Equal(0.000000, scores[1], precision: 6);
        Assert.Equal(0.000000, scores[2], precision: 6);
        Assert.Equal(0.251175, scores[3], precision: 6);
    }

    [Fact]
    public void Common_Term_Query_Matches_Bm25s_Lucene()
    {
        var b = IndexedReference();
        var scores = b.GetScores(new[] { "def" });
        Assert.Equal(0.147786, scores[0], precision: 6);
        Assert.Equal(0.147786, scores[1], precision: 6);
        Assert.Equal(0.000000, scores[2], precision: 6);
        Assert.Equal(0.129248, scores[3], precision: 6);
    }

    [Fact]
    public void Multi_Term_Query_Sums_Per_Term_Contributions()
    {
        var b = IndexedReference();
        var scores = b.GetScores(new[] { "authenticate", "token" });
        Assert.Equal(0.574401, scores[0], precision: 6);
        Assert.Equal(0.000000, scores[1], precision: 6);
        Assert.Equal(0.000000, scores[2], precision: 6);
        Assert.Equal(0.502351, scores[3], precision: 6);
    }

    [Fact]
    public void Repeated_Query_Tokens_Contribute_Additively()
    {
        var b = IndexedReference();
        var single = b.GetScores(new[] { "def" });
        var doubled = b.GetScores(new[] { "def", "def" });
        for (int i = 0; i < single.Length; i++)
            Assert.Equal(single[i] * 2, doubled[i], precision: 6);
    }

    [Fact]
    public void Unknown_Tokens_Score_Zero()
    {
        var b = IndexedReference();
        var scores = b.GetScores(new[] { "nonexistent" });
        Assert.All(scores, s => Assert.Equal(0.0, s));
    }

    [Fact]
    public void Empty_Query_Returns_All_Zero_Scores()
    {
        var b = IndexedReference();
        var scores = b.GetScores(Array.Empty<string>());
        Assert.Equal(ReferenceCorpus.Count, scores.Length);
        Assert.All(scores, s => Assert.Equal(0.0, s));
    }

    [Fact]
    public void Weight_Mask_Zeros_Excluded_Documents()
    {
        var b = IndexedReference();
        var mask = new[] { true, false, true, false };
        var scores = b.GetScores(new[] { "def" }, mask);
        Assert.Equal(0.147786, scores[0], precision: 6);
        Assert.Equal(0.0, scores[1]);
        Assert.Equal(0.0, scores[2]);
        Assert.Equal(0.0, scores[3]);
    }

    [Fact]
    public void Empty_Corpus_Yields_Empty_Score_Array()
    {
        var b = new Bm25();
        b.Index(new List<List<string>>());
        var scores = b.GetScores(new[] { "anything" });
        Assert.Empty(scores);
    }
}

public class SparseEnrichmentTests
{
    [Theory]
    [InlineData("src/auth.py", "def foo() auth auth src")]
    [InlineData("auth.py", "def foo() auth auth ")]
    [InlineData("a/b/c/d/e.py", "def foo() e e b c d")]
    [InlineData("/abs/foo.py", "def foo() foo foo abs")]
    [InlineData("a/./b/c.py", "def foo() c c a b")]
    public void EnrichForBm25_Matches_Python_Output(string path, string expected)
    {
        var chunk = MakeChunk("def foo()", path) with { EndLine = 1 };
        Assert.Equal(expected, Sparse.EnrichForBm25(chunk));
    }

    [Fact]
    public void SelectorToMask_Null_Returns_Null()
    {
        Assert.Null(Sparse.SelectorToMask(null, 5));
    }

    [Fact]
    public void SelectorToMask_Builds_Boolean_Mask()
    {
        var mask = Sparse.SelectorToMask(new[] { 0, 2, 4 }, 5);
        Assert.NotNull(mask);
        Assert.Equal(new[] { true, false, true, false, true }, mask);
    }
}
