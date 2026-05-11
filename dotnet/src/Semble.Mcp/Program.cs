using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Semble;
using Semble.Index;

namespace Semble.Mcp;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Parse: [path] [--ref REF]
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

        SembleMcpServer server;
        LocalIndexWatcher? watcher = null;
        try
        {
            IEncoder model = Dense.LoadModel();
            var cache = new IndexCache(model);
            if (path is not null)
            {
                try { await cache.GetAsync(path, @ref); }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync($"Pre-index of '{path}' failed: {ex.Message}");
                    return 1;
                }
                if (!Formatting.IsGitUrl(path))
                    watcher = new LocalIndexWatcher(cache, path);
            }
            server = new SembleMcpServer(cache, defaultSource: path);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            server = new SembleMcpServer(ex.Message);
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSingleton(server);
        builder.Services
            .AddMcpServer(opt =>
            {
                opt.ServerInfo = new() { Name = SembleMcpServer.ServerName, Version = "0.1.0" };
                opt.ServerInstructions = SembleMcpServer.ServerInstructions;
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync();
        return 0;
    }
}

[McpServerToolType]
internal static class SembleMcpTools
{
    [McpServerTool(Name = "search")]
    [Description(
        "Search a codebase with a natural-language or code query. " +
        "Pass a git URL or local path as `repo` to index it on demand; " +
        "indexes are cached for the session.")]
    public static Task<string> Search(
        SembleMcpServer server,
        [Description("Natural language or code query.")] string query,
        [Description("Git URL or local path. Required when no default index was set at startup.")] string? repo = null,
        [Description("Search mode: 'hybrid' (best for most queries), 'semantic', or 'bm25'.")] string mode = "hybrid",
        [Description("Number of results to return.")] int topK = 5)
        => server.SearchAsync(query, repo, mode, topK);

    [McpServerTool(Name = "find_related")]
    [Description(
        "Find code chunks semantically similar to a specific location in a file. " +
        "Use after `search` to explore related implementations.")]
    public static Task<string> FindRelated(
        SembleMcpServer server,
        [Description("Path to the file as stored in the index (use file_path from a search result).")] string filePath,
        [Description("Line number (1-indexed).")] int line,
        [Description("Git URL or local path. Required when no default index was set at startup.")] string? repo = null,
        [Description("Number of similar chunks to return.")] int topK = 5)
        => server.FindRelatedAsync(filePath, line, repo, topK);
}
