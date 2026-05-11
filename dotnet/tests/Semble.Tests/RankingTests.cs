using Semble.Ranking;
using Xunit;
using static Semble.Tests.Fixtures;

namespace Semble.Tests;

public class RerankTopKTests
{
    [Fact]
    public void Empty_Returns_Empty()
    {
        var ranked = Penalties.RerankTopK(new Dictionary<Chunk, double>(), topK: 5);
        Assert.Empty(ranked);
    }

    [Fact]
    public void Without_Path_Penalties_Respects_Raw_Scores()
    {
        var initChunk = MakeChunk("from .auth import authenticate", "src/semble/__init__.py");
        var implChunk = MakeChunk("def authenticate(token): ...", "src/semble/auth.py");
        var ranked = Penalties.RerankTopK(
            new Dictionary<Chunk, double> { [initChunk] = 2.0, [implChunk] = 1.0 },
            topK: 2,
            penalisePaths: false);
        Assert.Equal(initChunk, ranked[0].Chunk);
    }

    [Fact]
    public void Saturation_Decay_Keeps_Order()
    {
        var saturated = Enumerable.Range(0, 5)
            .Select(i => MakeChunk($"def fn_{i}(): pass", "big_file.py"))
            .ToList();
        var scores = new Dictionary<Chunk, double>();
        for (int i = 0; i < saturated.Count; i++)
            scores[saturated[i]] = (double)(5 - i);

        var ranked = Penalties.RerankTopK(scores, topK: 5);
        var rankedScores = ranked.Select(t => t.Score).ToList();
        var sortedDesc = rankedScores.OrderByDescending(s => s).ToList();
        Assert.Equal(sortedDesc, rankedScores);
    }

    [Theory]
    [InlineData("src/semble/__init__.py")]      // _REEXPORT_FILENAMES
    [InlineData("tests/test_auth.py")]           // _TEST_FILE_RE / _TEST_DIR_RE
    [InlineData("src/compat/old_api.py")]        // _COMPAT_DIR_RE
    [InlineData("examples/demo.py")]             // _EXAMPLES_DIR_RE
    [InlineData("src/types/index.d.ts")]         // _TYPE_DEFS_RE
    public void Demotes_Penalised_Paths(string penalisedPath)
    {
        var regular = MakeChunk("def impl(): pass", "src/regular.py");
        var penalised = MakeChunk("def impl(): pass", penalisedPath);
        var ranked = Penalties.RerankTopK(
            new Dictionary<Chunk, double> { [regular] = 1.0, [penalised] = 1.0 },
            topK: 2);
        Assert.Equal(regular, ranked[0].Chunk);
    }
}

public class ResolveAlphaTests
{
    [Theory]
    [InlineData("MyService", 0.7, 0.7)]              // explicit value returned as-is
    [InlineData("MyService", null, 0.3)]             // symbol query → _ALPHA_SYMBOL
    [InlineData("how does routing work", null, 0.5)] // NL query → _ALPHA_NL
    public void Returns_Explicit_Or_Auto_Detects(string query, double? alphaIn, double expected)
    {
        Assert.Equal(expected, Weighting.ResolveAlpha(query, alphaIn), precision: 9);
    }
}

public class ApplyQueryBoostTests
{
    [Theory]
    [InlineData("MyService")]                  // bare symbol query
    [InlineData("how does MyService work")]    // NL query with embedded symbol
    public void Boosts_Defining_Chunk(string query)
    {
        var defining = MakeChunk("class MyService:\n    pass", "src/my_service.py");
        var other = MakeChunk("x = MyService()", "src/utils.py");
        var scores = new Dictionary<Chunk, double> { [defining] = 0.5, [other] = 0.4 };

        var boosted = Boosting.ApplyQueryBoost(scores, query, new[] { defining, other });

        Assert.True(boosted[defining] > boosted[other]);
    }

    [Theory]
    [InlineData("MyService")]
    [InlineData("how does MyService work")]
    public void Scans_Non_Candidates(string query)
    {
        var defining = MakeChunk("class MyService:\n    pass", "src/myservice.py");
        var candidate = MakeChunk("x = 1", "src/other.py");
        var scores = new Dictionary<Chunk, double> { [candidate] = 0.5 };

        var boosted = Boosting.ApplyQueryBoost(scores, query, new[] { defining, candidate });

        Assert.True(boosted.ContainsKey(defining));
        Assert.True(boosted[defining] > 0);
    }

    [Theory]
    [InlineData("UserService")]                  // bare symbol query
    [InlineData("how does UserService work")]    // NL with embedded symbol
    public void Skips_Non_Matching_Stem(string query)
    {
        var defining = MakeChunk("class UserService:\n    pass", "src/user_service.py");
        var unrelated = MakeChunk("x = 1", "src/totally_unrelated_name.py");
        var scores = new Dictionary<Chunk, double> { [defining] = 0.5 };
        var boosted = Boosting.ApplyQueryBoost(scores, query, new[] { defining, unrelated });
        Assert.False(boosted.ContainsKey(unrelated));
    }

    [Theory]
    [InlineData("authenticate user session", "src/auth.py")]   // prefix / morphological
    [InlineData("auth service", "src/auth_service.py")]         // every keyword exact-matches a stem part
    public void Nl_Stem_Match_Boosts(string query, string filePath)
    {
        var chunk = MakeChunk("def authenticate(): pass", filePath);
        var scores = new Dictionary<Chunk, double> { [chunk] = 0.5 };
        var boosted = Boosting.ApplyQueryBoost(scores, query, new[] { chunk });
        Assert.True(boosted[chunk] > 0.5);
    }

    [Fact]
    public void Stopwords_Only_Query_Is_Noop()
    {
        var chunk = MakeChunk("def foo(): pass", "src/auth.py");
        var boosted = Boosting.ApplyQueryBoost(
            new Dictionary<Chunk, double> { [chunk] = 0.5 }, "the and or", new[] { chunk });
        Assert.Equal(0.5, boosted[chunk], precision: 9);
    }

    [Fact]
    public void Namespace_Qualified_Boosts_Leaf_Symbol()
    {
        var defining = MakeChunk("class Base:\n    pass", "src/base.py");
        var boosted = Boosting.ApplyQueryBoost(
            new Dictionary<Chunk, double> { [defining] = 0.5 }, "Sinatra::Base", new[] { defining });
        Assert.True(boosted[defining] > 0.5);
    }

    [Fact]
    public void Empty_Scores_Returns_Empty()
    {
        var boosted = Boosting.ApplyQueryBoost(
            new Dictionary<Chunk, double>(), "SomeQuery", Array.Empty<Chunk>());
        Assert.Empty(boosted);
    }

    // Exact-value cross-checks vs the Python implementation. Captured with the
    // upstream apply_query_boost on the same inputs:
    //   "MyService"               -> defining=2.75      other=0.4
    //   "how does MyService work" -> defining=1.625     other=0.4
    //   "Sinatra::Base"           -> base    =2.75

    [Fact]
    public void Symbol_Query_Numeric_Parity_With_Python()
    {
        var defining = MakeChunk("class MyService:\n    pass", "src/my_service.py");
        var other = MakeChunk("x = MyService()", "src/utils.py");
        var boosted = Boosting.ApplyQueryBoost(
            new Dictionary<Chunk, double> { [defining] = 0.5, [other] = 0.4 },
            "MyService",
            new[] { defining, other });
        Assert.Equal(2.75, boosted[defining], precision: 9);
        Assert.Equal(0.4, boosted[other], precision: 9);
    }

    [Fact]
    public void Embedded_Symbol_Numeric_Parity_With_Python()
    {
        var defining = MakeChunk("class MyService:\n    pass", "src/my_service.py");
        var other = MakeChunk("x = MyService()", "src/utils.py");
        var boosted = Boosting.ApplyQueryBoost(
            new Dictionary<Chunk, double> { [defining] = 0.5, [other] = 0.4 },
            "how does MyService work",
            new[] { defining, other });
        Assert.Equal(1.625, boosted[defining], precision: 9);
        Assert.Equal(0.4, boosted[other], precision: 9);
    }

    [Fact]
    public void Namespace_Qualified_Numeric_Parity_With_Python()
    {
        var defining = MakeChunk("class Base:\n    pass", "src/base.py");
        var boosted = Boosting.ApplyQueryBoost(
            new Dictionary<Chunk, double> { [defining] = 0.5 },
            "Sinatra::Base",
            new[] { defining });
        Assert.Equal(2.75, boosted[defining], precision: 9);
    }
}

public class BoostMultiChunkFilesTests
{
    [Fact]
    public void Empty_Is_Noop()
    {
        var empty = new Dictionary<Chunk, double>();
        Boosting.BoostMultiChunkFiles(empty);
        Assert.Empty(empty);
    }

    [Fact]
    public void All_Zero_Is_Noop()
    {
        var zeroChunk = MakeChunk("x = 1", "src/foo.py");
        var allZero = new Dictionary<Chunk, double> { [zeroChunk] = 0.0 };
        Boosting.BoostMultiChunkFiles(allZero);
        Assert.Equal(0.0, allZero[zeroChunk]);
    }

    [Fact]
    public void Promotes_Top_Chunk_Of_Multi_Chunk_File()
    {
        var c1 = MakeChunk("def a(): pass", "src/big.py");
        var c2 = MakeChunk("def b(): pass", "src/big.py");
        var c3 = MakeChunk("def c(): pass", "src/small.py");
        var scores = new Dictionary<Chunk, double> { [c1] = 1.0, [c2] = 0.8, [c3] = 1.0 };
        Boosting.BoostMultiChunkFiles(scores);
        Assert.True(scores[c1] > 1.0);
    }
}
