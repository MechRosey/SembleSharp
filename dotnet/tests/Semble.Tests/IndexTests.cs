using Semble.Index;
using Xunit;
using static Semble.Tests.Fixtures;

namespace Semble.Tests;

public class TmpProjectFixture : IDisposable
{
    public string Path { get; }

    public TmpProjectFixture()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "semble-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);

        File.WriteAllText(System.IO.Path.Combine(Path, "auth.py"),
            "def authenticate(token):\n" +
            "    \"\"\"Verify an auth token.\"\"\"\n" +
            "    return token == \"secret\"\n" +
            "\n" +
            "def login(username, password):\n" +
            "    return authenticate(password)\n");

        File.WriteAllText(System.IO.Path.Combine(Path, "utils.py"),
            "def format_name(first, last):\n" +
            "    return f\"{first} {last}\"\n" +
            "\n" +
            "class Config:\n" +
            "    debug = False\n" +
            "    host = \"localhost\"\n");

        File.WriteAllText(System.IO.Path.Combine(Path, "README.md"), "# Test project\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}

public class IndexCreationTests : IClassFixture<TmpProjectFixture>
{
    private readonly TmpProjectFixture _project;
    public IndexCreationTests(TmpProjectFixture project) { _project = project; }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Markdown_Inclusion_Honours_Flag(bool includeTextFiles, bool mdInResults)
    {
        var (_, _, chunks) = Create.CreateIndexFromPath(
            _project.Path, new MockEncoder(), includeTextFiles: includeTextFiles);
        var hasMd = chunks.Any(c =>
            System.IO.Path.GetExtension(c.FilePath).Equals(".md", StringComparison.Ordinal));
        Assert.Equal(mdInResults, hasMd);
    }

    [Fact]
    public void Indexing_Empty_Directory_Throws()
    {
        var emptyDir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "semble-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                Create.CreateIndexFromPath(emptyDir, new MockEncoder()));
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }
}

public class IndexedIndexTests : IClassFixture<TmpProjectFixture>
{
    private readonly SembleIndex _index;
    public IndexedIndexTests(TmpProjectFixture project)
    {
        _index = SembleIndex.FromPath(project.Path, model: new MockEncoder());
    }

    [Fact]
    public void Stats_Reports_Python_Language_With_At_Least_One_Chunk()
    {
        var stats = _index.Stats;
        Assert.Contains("python", stats.Languages.Keys);
        Assert.True(stats.Languages["python"] > 0);
    }

    [Theory]
    [InlineData("authenticate token", SearchMode.Hybrid)]
    [InlineData("authenticate", SearchMode.Bm25)]
    [InlineData("authentication", SearchMode.Semantic)]
    public void Search_Modes_Return_Bounded_List(string query, SearchMode mode)
    {
        var results = _index.Search(query, topK: 3, mode: mode);
        Assert.NotNull(results);
        Assert.True(results.Count <= 3);
    }

    [Fact]
    public void Search_Invalid_Mode_String_Throws()
    {
        Assert.Throws<ArgumentException>(() => _index.Search("query", mode: "invalid"));
    }

    [Fact]
    public void Search_Constraints_TopK_And_Dedup()
    {
        Assert.True(_index.Search("function", topK: 1, mode: SearchMode.Bm25).Count <= 1);

        var results = _index.Search("authenticate", topK: 5);
        var distinct = new HashSet<Chunk>(results.Select(r => r.Chunk));
        Assert.Equal(results.Count, distinct.Count);
    }

    [Theory]
    [InlineData(SearchMode.Bm25)]
    [InlineData(SearchMode.Hybrid)]
    [InlineData(SearchMode.Semantic)]
    public void Search_With_Filter_Paths_Restricts_Results(SearchMode mode)
    {
        var targetPath = _index.Chunks[^1].FilePath;
        var results = _index.Search("function", topK: 3, mode: mode, filterPaths: new[] { targetPath });
        Assert.All(results, r => Assert.Equal(targetPath, r.Chunk.FilePath));
    }

    [Theory]
    [InlineData(SearchMode.Bm25, "")]
    [InlineData(SearchMode.Bm25, "   ")]
    [InlineData(SearchMode.Bm25, "\n\n")]
    [InlineData(SearchMode.Hybrid, "")]
    [InlineData(SearchMode.Hybrid, "   ")]
    [InlineData(SearchMode.Hybrid, "\n\n")]
    [InlineData(SearchMode.Semantic, "")]
    [InlineData(SearchMode.Semantic, "   ")]
    [InlineData(SearchMode.Semantic, "\n\n")]
    public void Search_Empty_Query_Returns_Empty(SearchMode mode, string query)
    {
        Assert.Empty(_index.Search(query, mode: mode));
    }

    [Fact]
    public void FindRelated_From_Chunk_Excludes_Self()
    {
        var chunk = _index.Chunks[0];
        var viaChunk = _index.FindRelated(chunk, topK: 3);
        Assert.NotNull(viaChunk);
        Assert.True(viaChunk.Count <= 3);
        Assert.All(viaChunk, r => Assert.NotEqual(chunk, r.Chunk));
    }

    [Fact]
    public void FindRelated_From_SearchResult_Matches_From_Chunk()
    {
        var result = _index.Search("authenticate", topK: 1)[0];
        var viaResult = _index.FindRelated(result, topK: 3).Select(r => r.Chunk).ToList();
        var viaChunk = _index.FindRelated(result.Chunk, topK: 3).Select(r => r.Chunk).ToList();
        Assert.Equal(viaChunk, viaResult);
    }
}

public class FromPathRejectionTests
{
    [Fact]
    public void Missing_Path_Throws_DirectoryNotFoundException()
    {
        var bogus = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "semble-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<DirectoryNotFoundException>(() =>
            SembleIndex.FromPath(bogus, model: new MockEncoder()));
    }

    [Fact]
    public void File_Path_Throws_IOException()
    {
        var dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "semble-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = System.IO.Path.Combine(dir, "not_a_dir.py");
            File.WriteAllText(file, "x = 1\n");
            Assert.Throws<IOException>(() =>
                SembleIndex.FromPath(file, model: new MockEncoder()));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class FromGitTests : IDisposable
{
    private readonly string _tmp;
    private readonly GitRunner.Runner _originalRunner;

    public FromGitTests()
    {
        _tmp = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "semble-gittest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
        _originalRunner = GitRunner.CurrentRunner;
    }

    public void Dispose()
    {
        GitRunner.CurrentRunner = _originalRunner;
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
    }

    private string MakeLocalRepoWithMain()
    {
        RunGit(_tmp, "init", _tmp);
        var py = System.IO.Path.Combine(_tmp, "main.py");
        File.WriteAllText(py, "def hello():\n    return 'hello'\n");
        RunGit(_tmp, "add", "main.py");
        RunGit(_tmp, "commit", "-m", "add main");
        return _tmp;
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["GIT_AUTHOR_NAME"] = "test";
        psi.Environment["GIT_AUTHOR_EMAIL"] = "t@t.com";
        psi.Environment["GIT_COMMITTER_NAME"] = "test";
        psi.Environment["GIT_COMMITTER_EMAIL"] = "t@t.com";
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        psi.Environment["GIT_CONFIG_SYSTEM"] = "/dev/null";
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed with code {p.ExitCode}: {p.StandardError.ReadToEnd()}");
        }
    }

    [Fact]
    public void From_Git_Indexes_Local_Repo_With_Relative_Paths()
    {
        var repo = MakeLocalRepoWithMain();
        var idx = SembleIndex.FromGit(repo, model: new MockEncoder());
        Assert.True(idx.Stats.IndexedFiles >= 1);
        Assert.True(idx.Stats.TotalChunks > 0);
        Assert.Contains(idx.Chunks, c => c.FilePath.Contains("main.py", StringComparison.Ordinal));
        Assert.All(idx.Chunks, c => Assert.False(System.IO.Path.IsPathRooted(c.FilePath)));
    }

    [Fact]
    public void From_Git_With_Branch_Checks_Out_Specified_Ref()
    {
        var repo = System.IO.Path.Combine(_tmp, "repo");
        Directory.CreateDirectory(repo);
        RunGit(repo, "init");
        File.WriteAllText(System.IO.Path.Combine(repo, "main.py"), "def on_main(): pass\n");
        RunGit(repo, "add", "main.py");
        RunGit(repo, "commit", "-m", "main");
        RunGit(repo, "checkout", "-b", "feature");
        File.WriteAllText(System.IO.Path.Combine(repo, "feature.py"), "def on_feature(): pass\n");
        RunGit(repo, "add", "feature.py");
        RunGit(repo, "commit", "-m", "feature");

        var idx = SembleIndex.FromGit(repo, @ref: "feature", model: new MockEncoder());
        var fileNames = new HashSet<string>(idx.Chunks.Select(c =>
            System.IO.Path.GetFileName(c.FilePath)), StringComparer.Ordinal);
        Assert.Contains("feature.py", fileNames);
    }

    [Fact]
    public void From_Git_Bogus_Url_Raises_With_Clone_Failed_Message()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SembleIndex.FromGit("/nonexistent/path/that/does/not/exist", model: new MockEncoder()));
        Assert.Contains("git clone failed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void From_Git_When_Git_Missing_Raises_With_Specific_Message()
    {
        GitRunner.CurrentRunner = (_, _, _) =>
            throw new GitRunner.GitNotInstalledException();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SembleIndex.FromGit("https://github.com/x/y", model: new MockEncoder()));
        Assert.Contains("git is not installed", ex.Message, StringComparison.Ordinal);
    }
}
