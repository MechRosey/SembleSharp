using System.Reflection;

namespace Semble.Cli;

/// <summary>
/// Mirrors src/semble/cli.py. Entrypoints are split into Run* methods so tests
/// can drive them directly with mocked SembleIndex factories and captured
/// output streams (the .NET equivalent of the Python suite's
/// patch + monkeypatch + capsys workflow).
/// </summary>
public sealed class CliApp
{
    public static readonly string ClaudeFilePath =
        System.IO.Path.Combine(".claude", "agents", "semble-search.md");

    private static readonly HashSet<string> CliDispatchArgs = new(StringComparer.Ordinal)
    {
        "search", "find-related", "init", "download-model", "-h", "--help",
    };

    public Func<string, IEncoder?, SembleIndex> IndexFromPath { get; init; } =
        (path, model) => SembleIndex.FromPath(path, model);

    public Func<string, string?, IEncoder?, SembleIndex> IndexFromGit { get; init; } =
        (url, @ref, model) => SembleIndex.FromGit(url, @ref, model);

    public Func<Task> McpServeAsync { get; init; } = () => Task.CompletedTask;

    public Func<string, string, Action<string, long, long>?, Task> DownloadModelAsync { get; init; } =
        (modelId, destDir, progress) => Semble.Index.ModelDownloader.DownloadAsync(modelId, destDir, progress);

    public TextWriter Stdout { get; init; } = Console.Out;
    public TextWriter Stderr { get; init; } = Console.Error;
    public string WorkingDirectory { get; init; } = Directory.GetCurrentDirectory();

    public IEncoder? DefaultEncoder { get; init; }

    /// <summary>Top-level dispatcher. Returns the process exit code.</summary>
    public int Run(string[] args)
    {
        if (args.Length > 0 && CliDispatchArgs.Contains(args[0]))
            return RunCli(args);
        return RunMcp(args);
    }

    public int RunMcp(string[] args)
    {
        // path [--ref REF]
        string? path = null;
        string? @ref = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--ref" && i + 1 < args.Length)
            {
                @ref = args[i + 1];
                i++;
            }
            else if (!args[i].StartsWith('-'))
            {
                path = args[i];
            }
        }
        // The MCP serve hook is wired in Chunk 12; for now we no-op (matches
        // the Python `semble.mcp` import being optional).
        _ = path;
        _ = @ref;
        McpServeAsync().GetAwaiter().GetResult();
        return 0;
    }

    public int RunCli(string[] args)
    {
        if (args.Length == 0 || args[0] == "-h" || args[0] == "--help")
        {
            PrintTopLevelHelp();
            return 0;
        }

        return args[0] switch
        {
            "search" => RunSearch(args.AsSpan(1).ToArray()),
            "find-related" => RunFindRelated(args.AsSpan(1).ToArray()),
            "init" => RunInit(args.AsSpan(1).ToArray()),
            "download-model" => RunDownloadModel(args.AsSpan(1).ToArray()),
            _ => UnknownCommand(args[0]),
        };
    }

    private int UnknownCommand(string cmd)
    {
        Stderr.WriteLine($"Unknown command: {cmd}");
        return 2;
    }

    private void PrintTopLevelHelp()
    {
        Stdout.WriteLine("usage: semble {search,find-related,init,download-model} ...");
        Stdout.WriteLine();
        Stdout.WriteLine("subcommands:");
        Stdout.WriteLine("  search          Search a codebase.");
        Stdout.WriteLine("  find-related    Find code similar to a specific location.");
        Stdout.WriteLine("  init            Write .claude/agents/semble-search.md.");
        Stdout.WriteLine("  download-model  Download the default embedding model.");
    }

    private int RunSearch(string[] args)
    {
        if (!TryParseSearch(args, out var query, out var path, out var topK, out var mode, out var err))
        {
            Stderr.WriteLine(err);
            return 2;
        }
        var index = OpenIndex(path);
        var results = index.Search(query, mode!, topK: topK);
        if (results.Count == 0)
        {
            Stdout.WriteLine("No results found.");
        }
        else
        {
            var header = $"Search results for: '{query}' (mode={mode})";
            Stdout.Write(Formatting.FormatResults(header, results));
            Stdout.WriteLine();
        }
        return 0;
    }

    private int RunFindRelated(string[] args)
    {
        if (!TryParseFindRelated(args, out var filePath, out var line, out var path, out var topK, out var err))
        {
            Stderr.WriteLine(err);
            return 2;
        }
        var index = OpenIndex(path);
        var chunk = Formatting.ResolveChunk(index.Chunks, filePath, line);
        if (chunk is null)
        {
            Stderr.WriteLine($"No chunk found at {filePath}:{line}.");
            return 1;
        }
        var results = index.FindRelated(chunk, topK: topK);
        if (results.Count == 0)
        {
            Stdout.WriteLine($"No related chunks found for {filePath}:{line}.");
        }
        else
        {
            var header = $"Chunks related to {filePath}:{line}";
            Stdout.Write(Formatting.FormatResults(header, results));
            Stdout.WriteLine();
        }
        return 0;
    }

    private int RunInit(string[] args)
    {
        bool force = args.Any(a => a == "--force");
        return RunInitCore(force);
    }

    public int RunInitCore(bool force)
    {
        var dest = System.IO.Path.Combine(WorkingDirectory, ClaudeFilePath);
        if (File.Exists(dest) && !force)
        {
            Stderr.WriteLine($"{ClaudeFilePath} already exists. Run with --force to overwrite.");
            return 1;
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
        File.WriteAllText(dest, ReadEmbeddedAgentFile());
        Stdout.WriteLine($"Created {ClaudeFilePath}");
        return 0;
    }

    public static string ReadEmbeddedAgentFile()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith("semble-search.md", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public int RunDownloadModel(string[] args)
    {
        string? modelId = null;
        string? destDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--model-id" || args[i] == "-m") && i + 1 < args.Length)
            {
                modelId = args[++i];
            }
            else if ((args[i] == "--dest" || args[i] == "-d") && i + 1 < args.Length)
            {
                destDir = args[++i];
            }
            else if (args[i] == "-h" || args[i] == "--help")
            {
                Stdout.WriteLine("usage: semble download-model [--model-id ID] [--dest DIR]");
                Stdout.WriteLine();
                Stdout.WriteLine("  --model-id  HuggingFace model repo (default: minishlab/potion-code-16M)");
                Stdout.WriteLine("  --dest      Local directory (default: ~/.cache/semble/<model-id>)");
                return 0;
            }
        }

        modelId ??= Semble.Index.Dense.DefaultModelName;
        if (string.IsNullOrEmpty(destDir))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            destDir = System.IO.Path.Combine(home, ".cache", "semble", modelId);
        }

        Stdout.WriteLine($"Downloading {modelId} to {destDir} ...");
        try
        {
            DownloadModelAsync(
                modelId,
                destDir,
                (file, recv, total) =>
                {
                    string bar = total > 0
                        ? $" {recv * 100 / total,3}%"
                        : $" {recv / 1024 / 1024} MB";
                    Stdout.Write($"\r  {file}{bar}    ");
                }).GetAwaiter().GetResult();
            Stdout.WriteLine();
            Stdout.WriteLine($"Done. Set SEMBLE_MODEL_PATH={destDir} or pass --model-path to use a non-default location.");
            return 0;
        }
        catch (Exception ex)
        {
            Stderr.WriteLine($"download-model failed: {ex.Message}");
            return 1;
        }
    }

    private SembleIndex OpenIndex(string path)
    {
        return Formatting.IsGitUrl(path)
            ? IndexFromGit(path, null, DefaultEncoder)
            : IndexFromPath(path, DefaultEncoder);
    }

    private static bool TryParseSearch(
        string[] args,
        out string query,
        out string path,
        out int topK,
        out string mode,
        out string err)
    {
        query = ""; path = "."; topK = 5; mode = "hybrid"; err = "";
        var positionals = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "-k" || a == "--top-k")
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out topK))
                {
                    err = $"Expected integer after {a}.";
                    return false;
                }
                i++;
            }
            else if (a == "-m" || a == "--mode")
            {
                if (i + 1 >= args.Length)
                {
                    err = $"Expected value after {a}.";
                    return false;
                }
                mode = args[i + 1];
                i++;
            }
            else
            {
                positionals.Add(a);
            }
        }
        if (positionals.Count < 1)
        {
            err = "search: missing required argument 'query'.";
            return false;
        }
        query = positionals[0];
        if (positionals.Count >= 2)
            path = positionals[1];
        if (mode != "hybrid" && mode != "semantic" && mode != "bm25")
        {
            err = $"search: invalid mode '{mode}'.";
            return false;
        }
        return true;
    }

    private static bool TryParseFindRelated(
        string[] args,
        out string filePath,
        out int line,
        out string path,
        out int topK,
        out string err)
    {
        filePath = ""; line = 0; path = "."; topK = 5; err = "";
        var positionals = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "-k" || a == "--top-k")
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out topK))
                {
                    err = $"Expected integer after {a}.";
                    return false;
                }
                i++;
            }
            else
            {
                positionals.Add(a);
            }
        }
        if (positionals.Count < 2)
        {
            err = "find-related: missing required arguments 'file_path' and 'line'.";
            return false;
        }
        filePath = positionals[0];
        if (!int.TryParse(positionals[1], out line))
        {
            err = "find-related: 'line' must be an integer.";
            return false;
        }
        if (positionals.Count >= 3)
            path = positionals[2];
        return true;
    }
}
