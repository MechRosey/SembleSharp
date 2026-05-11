namespace Semble.Index;

public enum FileCategory
{
    Code,
    Document,
}

/// <summary>Language and indexing policy for a file extension.</summary>
public readonly record struct FileType(string Language, FileCategory Category);

/// <summary>Mirrors src/semble/index/file_walker.py.</summary>
public static class FileWalker
{
    public static readonly IReadOnlyDictionary<string, FileType> FileTypes = new Dictionary<string, FileType>(StringComparer.Ordinal)
    {
        [".py"] = new("python", FileCategory.Code),
        [".js"] = new("javascript", FileCategory.Code),
        [".jsx"] = new("javascript", FileCategory.Code),
        [".ts"] = new("typescript", FileCategory.Code),
        [".tsx"] = new("typescript", FileCategory.Code),
        [".go"] = new("go", FileCategory.Code),
        [".rs"] = new("rust", FileCategory.Code),
        [".java"] = new("java", FileCategory.Code),
        [".kt"] = new("kotlin", FileCategory.Code),
        [".kts"] = new("kotlin", FileCategory.Code),
        [".rb"] = new("ruby", FileCategory.Code),
        [".php"] = new("php", FileCategory.Code),
        [".c"] = new("c", FileCategory.Code),
        [".h"] = new("c", FileCategory.Code),
        [".cpp"] = new("cpp", FileCategory.Code),
        [".hpp"] = new("cpp", FileCategory.Code),
        [".cs"] = new("csharp", FileCategory.Code),
        [".swift"] = new("swift", FileCategory.Code),
        [".scala"] = new("scala", FileCategory.Code),
        [".sbt"] = new("scala", FileCategory.Code),
        [".ex"] = new("elixir", FileCategory.Code),
        [".exs"] = new("elixir", FileCategory.Code),
        [".dart"] = new("dart", FileCategory.Code),
        [".lua"] = new("lua", FileCategory.Code),
        [".sql"] = new("sql", FileCategory.Code),
        [".sh"] = new("bash", FileCategory.Code),
        [".bash"] = new("bash", FileCategory.Code),
        [".zig"] = new("zig", FileCategory.Code),
        [".hs"] = new("haskell", FileCategory.Code),
        [".md"] = new("markdown", FileCategory.Document),
        [".yaml"] = new("yaml", FileCategory.Document),
        [".yml"] = new("yaml", FileCategory.Document),
        [".toml"] = new("toml", FileCategory.Document),
        [".json"] = new("json", FileCategory.Document),
        // Binary document formats — text content is fetched via an
        // ITextExtractor (markitdown / pdftotext); chunking dispatches on
        // the extractor's output language, not on the entries here.
        [".pdf"] = new("pdf", FileCategory.Document),
        [".docx"] = new("docx", FileCategory.Document),
        [".xlsx"] = new("xlsx", FileCategory.Document),
        [".pptx"] = new("pptx", FileCategory.Document),
    };

    public static readonly IReadOnlySet<string> DefaultIgnoredDirs = new HashSet<string>(StringComparer.Ordinal)
    {
        ".git", ".hg", ".svn", "__pycache__", "node_modules", ".venv", "venv",
        ".tox", ".mypy_cache", ".pytest_cache", ".ruff_cache", ".cache",
        ".semble", "dist", "build", ".eggs",
    };

    /// <summary>Return the language for a file path, or null for unknown extensions.</summary>
    public static string? LanguageForPath(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return FileTypes.TryGetValue(ext, out var spec) ? spec.Language : null;
    }

    /// <summary>Return the set of file extensions to index.</summary>
    public static IReadOnlySet<string> FilterExtensions(
        IReadOnlySet<string>? extensions,
        bool includeTextFiles)
    {
        if (extensions is not null)
            return extensions;
        var categories = new HashSet<FileCategory> { FileCategory.Code };
        if (includeTextFiles)
            categories.Add(FileCategory.Document);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (ext, spec) in FileTypes)
        {
            if (categories.Contains(spec.Category))
                result.Add(ext);
        }
        return result;
    }

    /// <summary>
    /// Yield files under <paramref name="root"/> matching <paramref name="extensions"/>,
    /// skipping default-ignored dirs, user-supplied ignored dir names, and root .gitignore.
    /// </summary>
    /// <remarks>
    /// Directory entries whose canonical (symlink-resolved) path falls outside
    /// <paramref name="root"/> are not descended into. This prevents a malicious
    /// repo containing a directory symlink (e.g. <c>notes -> /home/user/.config</c>)
    /// from causing the walker to read files outside the supplied root.
    /// File-level symlinks are still followed by <see cref="File.ReadAllText(string)"/>
    /// at chunk-read time — same as the upstream Python implementation.
    /// </remarks>
    public static IEnumerable<string> WalkFiles(
        string root,
        IReadOnlySet<string> extensions,
        IReadOnlySet<string>? ignore = null)
    {
        var ignoreDirs = new HashSet<string>(DefaultIgnoredDirs, StringComparer.Ordinal);
        if (ignore is not null)
            ignoreDirs.UnionWith(ignore);

        var gitignore = LoadRootGitignore(root);
        var canonicalRoot = Canonicalize(root);
        return Walk(root, root, extensions, ignoreDirs, gitignore, canonicalRoot);
    }

    private static GitIgnoreMatcher? LoadRootGitignore(string root)
    {
        var gitignore = System.IO.Path.Combine(root, ".gitignore");
        if (!File.Exists(gitignore))
            return null;
        var lines = File.ReadAllLines(gitignore);
        return new GitIgnoreMatcher(lines);
    }

    private static IEnumerable<string> Walk(
        string root,
        string current,
        IReadOnlySet<string> extensions,
        IReadOnlySet<string> ignoreDirs,
        GitIgnoreMatcher? gitignore,
        string canonicalRoot)
    {
        string[] dirs;
        string[] files;
        try
        {
            dirs = Directory.GetDirectories(current);
            files = Directory.GetFiles(current);
        }
        catch (DirectoryNotFoundException)
        {
            yield break;
        }

        var relDir = System.IO.Path.GetRelativePath(root, current).Replace('\\', '/');
        if (relDir == ".")
            relDir = "";

        var keptDirs = new List<string>();
        foreach (var dir in dirs)
        {
            var dirName = System.IO.Path.GetFileName(dir);
            if (ignoreDirs.Contains(dirName))
                continue;
            var rel = (relDir.Length == 0 ? dirName : relDir + "/" + dirName) + "/";
            if (gitignore?.IsIgnored(rel) == true)
                continue;
            // Symlink defense: refuse to descend into directories whose
            // canonical (symlink-resolved) path falls outside the original
            // root. Walking each step's parent stayed inside-root by
            // induction, so we only need to check the immediate child here:
            // any deeper-tree symlink pointing outside root will be caught on
            // the recursion's next pass.
            if (!IsInsideCanonicalRoot(dir, canonicalRoot))
                continue;
            keptDirs.Add(dir);
        }

        Array.Sort(files, StringComparer.Ordinal);
        foreach (var file in files)
        {
            var fileName = System.IO.Path.GetFileName(file);
            var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
            if (!extensions.Contains(ext))
                continue;
            if (gitignore is not null)
            {
                var relFile = relDir.Length == 0 ? fileName : relDir + "/" + fileName;
                if (gitignore.IsIgnored(relFile))
                    continue;
            }
            yield return file;
        }

        foreach (var dir in keptDirs)
        {
            foreach (var f in Walk(root, dir, extensions, ignoreDirs, gitignore, canonicalRoot))
                yield return f;
        }
    }

    /// <summary>
    /// Path comparison mode for the host filesystem — case-insensitive on
    /// Windows, case-sensitive on Linux. macOS is mixed in practice (HFS+/APFS
    /// are case-insensitive by default but case-sensitive volumes exist); we
    /// pick the conservative case-sensitive default there. The check rejects
    /// out-of-root paths in both directions so a stricter comparison only
    /// risks rejecting legitimate symlinks differing in case, not letting an
    /// attacker through.
    /// </summary>
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// Resolve <paramref name="path"/> through any symlinks at the final
    /// component and return its absolute canonical form. Earlier components
    /// are not resolved by this single call; the walker invokes
    /// <see cref="IsInsideCanonicalRoot"/> at every descent step, so an
    /// inductive argument covers parent symlinks: every directory we
    /// descended into has already been verified to canonicalise inside root.
    /// </summary>
    private static string Canonicalize(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            return System.IO.Path.GetFullPath(target?.FullName ?? path);
        }
        catch
        {
            // Permission / IO errors fall back to the textual full path; the
            // caller's ancestry check still applies.
            return System.IO.Path.GetFullPath(path);
        }
    }

    private static bool IsInsideCanonicalRoot(string candidate, string canonicalRoot)
    {
        var canonicalCandidate = Canonicalize(candidate);
        if (string.Equals(canonicalCandidate, canonicalRoot, PathComparison))
            return true;
        var prefix = canonicalRoot.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? canonicalRoot
            : canonicalRoot + System.IO.Path.DirectorySeparatorChar;
        return canonicalCandidate.StartsWith(prefix, PathComparison);
    }
}
