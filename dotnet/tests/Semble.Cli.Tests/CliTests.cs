using Semble;
using Semble.Cli;
using Xunit;
using static Semble.Tests.Fixtures;

namespace Semble.Cli.Tests;

/// <summary>
/// Helper that builds a small in-memory SembleIndex over a fixed chunk list,
/// suitable for the CLI tests that previously mocked SembleIndex with MagicMock.
/// </summary>
internal static class FakeIndex
{
    public static SembleIndex Build(IReadOnlyList<Chunk> chunks)
    {
        var encoder = new MockEncoder();
        var embeddings = encoder.Encode(chunks.Select(c => c.Content).ToList());
        var dense = new global::Semble.Index.DenseBackend(embeddings);
        var bm25 = new global::Semble.Index.Bm25();
        bm25.Index(chunks
            .Select(c => (IReadOnlyList<string>)Tokens.Tokenize(c.Content))
            .ToList());
        return new SembleIndex(encoder, bm25, dense, chunks);
    }
}

public class CliSearchTests
{
    [Fact]
    public void Search_With_Results_Prints_Query_And_Score()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var chunk = MakeChunk("def foo(): pass", "src/foo.py");
        var index = FakeIndex.Build(new[] { chunk });

        var app = new CliApp
        {
            IndexFromPath = (_, _) => index,
            Stdout = stdout,
            Stderr = stderr,
        };

        var rc = app.Run(new[] { "search", "query text", "/some/path" });
        Assert.Equal(0, rc);
        var s = stdout.ToString();
        Assert.Contains("query text", s, StringComparison.Ordinal);
        Assert.Matches(@"score=\d+\.\d{3}", s); // mirrors the Python "0.9" check structurally
        Assert.Contains("src/foo.py", s, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_No_Results_Prints_Friendly_Message()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var chunk = MakeChunk("def foo(): pass", "src/foo.py");
        var index = FakeIndex.Build(new[] { chunk });

        var app = new CliApp
        {
            IndexFromPath = (_, _) => index,
            Stdout = stdout,
            Stderr = stderr,
        };

        // "zzzznonexistentterm" tokenises but matches nothing in the corpus,
        // so BM25 returns no results.
        var rc = app.Run(new[] { "search", "zzzznonexistentterm", "/some/path", "--top-k", "3", "--mode", "bm25" });
        Assert.Equal(0, rc);
        Assert.Contains("No results found", stdout.ToString(), StringComparison.Ordinal);
    }
}

public class CliFindRelatedTests
{
    [Fact]
    public void Find_Related_With_Results_Prints_File_And_Score()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var chunk = MakeChunk("class Bar: pass", "src/bar.py");
        var index = FakeIndex.Build(new[] { chunk, MakeChunk("class Other: pass", "src/other.py") });

        var app = new CliApp
        {
            IndexFromPath = (_, _) => index,
            Stdout = stdout,
            Stderr = stderr,
        };

        var rc = app.Run(new[] { "find-related", "src/bar.py", "1", "/some/path" });
        Assert.Equal(0, rc);
        var s = stdout.ToString();
        // The single related chunk found should be the "other" file (we exclude self).
        Assert.Contains("src/other.py", s, StringComparison.Ordinal);
    }

    [Fact]
    public void Find_Related_No_Results_Prints_Friendly_Message()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        // Index only contains the seed chunk, so find_related finds none.
        var chunk = MakeChunk("class Bar: pass", "src/bar.py");
        var index = FakeIndex.Build(new[] { chunk });

        var app = new CliApp
        {
            IndexFromPath = (_, _) => index,
            Stdout = stdout,
            Stderr = stderr,
        };
        var rc = app.Run(new[] { "find-related", "src/bar.py", "1", "/some/path" });
        Assert.Equal(0, rc);
        Assert.Contains("No related chunks found", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Find_Related_Unknown_Chunk_Exits_With_Code_One()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var index = FakeIndex.Build(new[] { MakeChunk("class Bar: pass", "src/bar.py") });

        var app = new CliApp
        {
            IndexFromPath = (_, _) => index,
            Stdout = stdout,
            Stderr = stderr,
        };
        var rc = app.Run(new[] { "find-related", "unknown.py", "1", "/some/path" });
        Assert.Equal(1, rc);
        Assert.Contains("No chunk found", stderr.ToString(), StringComparison.Ordinal);
    }
}

public class CliInitTests : IDisposable
{
    private readonly string _tmp;

    public CliInitTests()
    {
        _tmp = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "semble-init-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Init_Creates_Agent_File_And_Prints_Path()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var app = new CliApp { WorkingDirectory = _tmp, Stdout = stdout, Stderr = stderr };

        var rc = app.RunInitCore(force: false);
        Assert.Equal(0, rc);
        var dest = System.IO.Path.Combine(_tmp, CliApp.ClaudeFilePath);
        Assert.True(File.Exists(dest));
        Assert.Equal(CliApp.ReadEmbeddedAgentFile(), File.ReadAllText(dest));
        Assert.Contains(CliApp.ClaudeFilePath, stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Init_Refuses_Overwrite_Without_Force()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var app = new CliApp { WorkingDirectory = _tmp, Stdout = stdout, Stderr = stderr };

        Assert.Equal(0, app.RunInitCore(force: false));
        var rc2 = app.RunInitCore(force: false);
        Assert.Equal(1, rc2);
        Assert.Contains("already exists", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Init_Overwrites_With_Force()
    {
        var dest = System.IO.Path.Combine(_tmp, CliApp.ClaudeFilePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
        File.WriteAllText(dest, "old content");

        var app = new CliApp { WorkingDirectory = _tmp };
        var rc = app.RunInitCore(force: true);
        Assert.Equal(0, rc);
        Assert.Equal(CliApp.ReadEmbeddedAgentFile(), File.ReadAllText(dest));
    }

    [Fact]
    public void Init_Via_Cli_Routes_Through_Run()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var app = new CliApp { WorkingDirectory = _tmp, Stdout = stdout, Stderr = stderr };
        var rc = app.Run(new[] { "init" });
        Assert.Equal(0, rc);
        Assert.True(File.Exists(System.IO.Path.Combine(_tmp, CliApp.ClaudeFilePath)));
        Assert.Contains(CliApp.ClaudeFilePath, stdout.ToString(), StringComparison.Ordinal);
    }
}

public class CliDispatchTests
{
    [Fact]
    public void Top_Level_Help_Returns_Zero_And_Lists_Subcommands()
    {
        var stdout = new StringWriter();
        var app = new CliApp { Stdout = stdout, Stderr = new StringWriter() };
        var rc = app.Run(new[] { "--help" });
        Assert.Equal(0, rc);
        Assert.Contains("find-related", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Top_Level_Without_Subcommand_Routes_To_Mcp_Hook()
    {
        var called = false;
        var app = new CliApp
        {
            McpServeAsync = () => { called = true; return Task.CompletedTask; },
            Stdout = new StringWriter(),
            Stderr = new StringWriter(),
        };
        var rc = app.Run(new[] { "/some/path" });
        Assert.Equal(0, rc);
        Assert.True(called);
    }

    [Fact]
    public void Search_Subcommand_Routes_To_Cli()
    {
        var stdout = new StringWriter();
        var index = FakeIndex.Build(new[] { MakeChunk("def foo(): pass", "src/foo.py") });
        var app = new CliApp
        {
            IndexFromPath = (_, _) => index,
            Stdout = stdout,
            Stderr = new StringWriter(),
        };
        var rc = app.Run(new[] { "search", "query text", "/some/path" });
        Assert.Equal(0, rc);
        Assert.Contains("query text", stdout.ToString(), StringComparison.Ordinal);
    }
}

public class CliDownloadModelTests
{
    [Fact]
    public void DownloadModel_Help_Returns_Zero_And_Prints_Usage()
    {
        var stdout = new StringWriter();
        var app = new CliApp { Stdout = stdout, Stderr = new StringWriter() };
        var rc = app.Run(new[] { "download-model", "--help" });
        Assert.Equal(0, rc);
        Assert.Contains("download-model", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadModel_Calls_Downloader_With_Default_ModelId()
    {
        string? capturedModelId = null;
        string? capturedDestDir = null;
        var stdout = new StringWriter();
        var app = new CliApp
        {
            Stdout = stdout,
            Stderr = new StringWriter(),
            DownloadModelAsync = (modelId, destDir, _) =>
            {
                capturedModelId = modelId;
                capturedDestDir = destDir;
                return Task.CompletedTask;
            },
        };
        var rc = app.Run(new[] { "download-model", "--dest", "/tmp/model" });
        Assert.Equal(0, rc);
        Assert.Equal(Semble.Index.Dense.DefaultModelName, capturedModelId);
        Assert.Equal("/tmp/model", capturedDestDir);
    }

    [Fact]
    public void DownloadModel_Calls_Downloader_With_Explicit_ModelId_And_Dest()
    {
        string? capturedModelId = null;
        string? capturedDestDir = null;
        var stdout = new StringWriter();
        var app = new CliApp
        {
            Stdout = stdout,
            Stderr = new StringWriter(),
            DownloadModelAsync = (modelId, destDir, _) =>
            {
                capturedModelId = modelId;
                capturedDestDir = destDir;
                return Task.CompletedTask;
            },
        };
        var rc = app.Run(new[] { "download-model", "--model-id", "org/my-model", "--dest", "/tmp/my-model" });
        Assert.Equal(0, rc);
        Assert.Equal("org/my-model", capturedModelId);
        Assert.Equal("/tmp/my-model", capturedDestDir);
    }

    [Fact]
    public void DownloadModel_Downloader_Failure_Prints_Error_And_Returns_One()
    {
        var stderr = new StringWriter();
        var app = new CliApp
        {
            Stdout = new StringWriter(),
            Stderr = stderr,
            DownloadModelAsync = (_, _, _) => Task.FromException(new HttpRequestException("connection refused")),
        };
        var rc = app.Run(new[] { "download-model", "--dest", "/tmp/model" });
        Assert.Equal(1, rc);
        Assert.Contains("connection refused", stderr.ToString(), StringComparison.Ordinal);
    }
}

public class AgentFileTests
{
    [Fact]
    public void Tools_Line_Lists_Only_Bash_And_Read()
    {
        var content = CliApp.ReadEmbeddedAgentFile();
        var frontmatterParts = content.Split("---", StringSplitOptions.None);
        Assert.True(frontmatterParts.Length >= 2);
        var frontmatter = frontmatterParts[1];
        var toolsLine = frontmatter
            .Split('\n')
            .First(line => line.StartsWith("tools:", StringComparison.Ordinal));
        var tools = toolsLine
            .Substring("tools:".Length)
            .Split(',')
            .Select(t => t.Trim())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new HashSet<string> { "Bash", "Read" }, tools);
        Assert.DoesNotContain(tools, t => t.Contains("mcp__", StringComparison.Ordinal));
    }
}
