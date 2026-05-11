using Semble.Index;
using Xunit;

namespace Semble.Tests;

public class GitIgnoreMatcherTests
{
    [Theory]
    [InlineData("local/", "local/", true)]
    [InlineData("local/", "local/anything.py", false)] // not a dir probe; cascade is the walker's job
    [InlineData("generated.py", "generated.py", true)]
    [InlineData("generated.py", "src/keep.py", false)]
    [InlineData("out/*", "out/", false)]
    [InlineData("out/*", "out/a.py", true)]
    public void Single_Pattern(string pattern, string probe, bool ignored)
    {
        var m = new GitIgnoreMatcher(new[] { pattern });
        Assert.Equal(ignored, m.IsIgnored(probe));
    }

    [Fact]
    public void Negation_Reincludes_Previously_Ignored_File()
    {
        var m = new GitIgnoreMatcher(new[] { "out/*", "!out/keep.py" });
        Assert.True(m.IsIgnored("out/a.py"));
        Assert.False(m.IsIgnored("out/keep.py"));
    }

    [Fact]
    public void Comments_And_Blank_Lines_Are_Ignored()
    {
        var m = new GitIgnoreMatcher(new[] { "# a comment", "", "   ", "out/*" });
        Assert.True(m.IsIgnored("out/x"));
    }
}

public class WalkFilesTests : IDisposable
{
    private readonly string _tmp;

    public WalkFilesTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "semble-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
    }

    private void Touch(string relative, string content = "x = 1\n")
    {
        var full = Path.Combine(_tmp, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private HashSet<string> WalkSet(IReadOnlySet<string>? extensions = null)
    {
        var exts = extensions ?? new HashSet<string>(StringComparer.Ordinal) { ".py" };
        return FileWalker.WalkFiles(_tmp, exts)
            .Select(p => Path.GetRelativePath(_tmp, p).Replace(Path.DirectorySeparatorChar, '/'))
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void Default_Ignored_Dirs_Are_Always_Skipped()
    {
        Touch("src/a.py");
        Touch(".venv/lib/b.py");
        Touch("node_modules/pkg/c.py");
        Touch(".cache/uv/d.py");
        Touch(".next/server/chunks/page.py");
        Assert.Equal(new HashSet<string> { "src/a.py" }, WalkSet());
    }

    [Fact]
    public void Root_Gitignore_Excludes_Directories_And_Files()
    {
        Touch("src/keep.py");
        Touch("local/ignored.py");
        Touch("generated.py");
        File.WriteAllText(Path.Combine(_tmp, ".gitignore"), "local/\ngenerated.py\n");
        Assert.Equal(new HashSet<string> { "src/keep.py" }, WalkSet());
    }

    [Fact]
    public void Negation_Patterns_Reinclude_Previously_Ignored_Files()
    {
        Touch("out/a.py");
        Touch("out/keep.py");
        File.WriteAllText(Path.Combine(_tmp, ".gitignore"), "out/*\n!out/keep.py\n");
        Assert.Equal(new HashSet<string> { "out/keep.py" }, WalkSet());
    }

    [Fact]
    public void Ignored_Directories_Are_Not_Descended_Into()
    {
        Touch("src/a.py");
        Touch("node_modules/deep/deeper/b.js");
        var exts = new HashSet<string>(StringComparer.Ordinal) { ".py", ".js" };
        var found = WalkSet(exts);
        Assert.DoesNotContain(found, p => p.Contains("node_modules", StringComparison.Ordinal));
    }

    [Fact]
    public void Symlinked_Directory_Pointing_Outside_Root_Is_Skipped()
    {
        // Symlinks need administrator on Windows; skip there.
        if (OperatingSystem.IsWindows())
            return;

        var outside = Path.Combine(Path.GetTempPath(), "semble-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.py"), "API_KEY = 'leaked'\n");
            File.WriteAllText(Path.Combine(_tmp, "real.py"), "ok = 1\n");
            Directory.CreateSymbolicLink(Path.Combine(_tmp, "evil_link"), outside);

            var found = WalkSet();
            Assert.Contains("real.py", found);
            Assert.DoesNotContain(found, p => p.Contains("secret.py", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Symlinked_Directory_Pointing_Inside_Root_Is_Followed()
    {
        // In-tree symlinks (e.g. `latest -> versions/v3`) are legitimate and
        // should still be walked. The defense only excludes paths whose
        // canonical resolution escapes the root.
        if (OperatingSystem.IsWindows())
            return;

        Touch("real_dir/inside.py");
        Directory.CreateSymbolicLink(
            Path.Combine(_tmp, "alias_link"),
            Path.Combine(_tmp, "real_dir"));

        var found = WalkSet();
        Assert.Contains("real_dir/inside.py", found);
        Assert.Contains("alias_link/inside.py", found);
    }

    [Fact]
    public void Deeper_Tree_Symlink_Pointing_Outside_Is_Caught_On_Recursion()
    {
        // A real subdirectory containing a symlink that points out of root
        // should still walk the real subdir; only the escape symlink is skipped.
        if (OperatingSystem.IsWindows())
            return;

        var outside = Path.Combine(Path.GetTempPath(), "semble-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "stolen.py"), "stolen = True\n");
            Touch("subdir/legit.py");
            Directory.CreateSymbolicLink(Path.Combine(_tmp, "subdir", "escape"), outside);

            var found = WalkSet();
            Assert.Contains("subdir/legit.py", found);
            Assert.DoesNotContain(found, p => p.Contains("stolen.py", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { /* best effort */ }
        }
    }
}

public class LanguageForPathTests
{
    [Theory]
    [InlineData("foo.py", "python")]
    [InlineData("foo.PY", "python")]
    [InlineData("a/b/c.ts", "typescript")]
    [InlineData("Main.kt", "kotlin")]
    [InlineData("README.md", "markdown")]
    public void Returns_Language_For_Known_Extensions(string path, string expected)
    {
        Assert.Equal(expected, FileWalker.LanguageForPath(path));
    }

    [Fact]
    public void Returns_Null_For_Unknown_Extension()
    {
        Assert.Null(FileWalker.LanguageForPath("foo.xyz"));
    }
}

public class FilterExtensionsTests
{
    [Fact]
    public void Returns_Caller_Set_When_Provided()
    {
        var caller = new HashSet<string>(StringComparer.Ordinal) { ".lol" };
        var result = FileWalker.FilterExtensions(caller, includeTextFiles: false);
        Assert.Same(caller, result);
    }

    [Fact]
    public void Defaults_Code_Only_When_IncludeTextFiles_False()
    {
        var result = FileWalker.FilterExtensions(null, includeTextFiles: false);
        Assert.Contains(".py", result);
        Assert.DoesNotContain(".md", result);
        Assert.DoesNotContain(".json", result);
    }

    [Fact]
    public void Includes_Documents_When_IncludeTextFiles_True()
    {
        var result = FileWalker.FilterExtensions(null, includeTextFiles: true);
        Assert.Contains(".py", result);
        Assert.Contains(".md", result);
        Assert.Contains(".yaml", result);
    }
}
