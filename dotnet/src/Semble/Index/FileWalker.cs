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
    public static IEnumerable<string> WalkFiles(
        string root,
        IReadOnlySet<string> extensions,
        IReadOnlySet<string>? ignore = null)
    {
        var ignoreDirs = new HashSet<string>(DefaultIgnoredDirs, StringComparer.Ordinal);
        if (ignore is not null)
            ignoreDirs.UnionWith(ignore);

        var gitignore = LoadRootGitignore(root);
        return Walk(root, root, extensions, ignoreDirs, gitignore);
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
        GitIgnoreMatcher? gitignore)
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
            foreach (var f in Walk(root, dir, extensions, ignoreDirs, gitignore))
                yield return f;
        }
    }
}
