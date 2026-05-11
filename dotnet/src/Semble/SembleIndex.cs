using Semble.Index;

namespace Semble;

/// <summary>
/// Fast local code index with hybrid search. Mirrors src/semble/index/index.py.
/// </summary>
public sealed class SembleIndex
{
    private readonly Bm25 _bm25;
    private readonly DenseBackend _semantic;
    private readonly Dictionary<string, List<int>> _fileMapping;
    private readonly Dictionary<string, List<int>> _languageMapping;

    public IEncoder Model { get; }
    public IReadOnlyList<Chunk> Chunks { get; }

    /// <summary>Internal constructor — use <see cref="FromPath"/> or <see cref="FromGit"/>.</summary>
    public SembleIndex(IEncoder model, Bm25 bm25, DenseBackend semantic, IReadOnlyList<Chunk> chunks)
    {
        Model = model;
        _bm25 = bm25;
        _semantic = semantic;
        Chunks = chunks;

        _fileMapping = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        _languageMapping = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            if (!string.IsNullOrEmpty(c.Language))
            {
                if (!_languageMapping.TryGetValue(c.Language!, out var langList))
                    _languageMapping[c.Language!] = langList = new List<int>();
                langList.Add(i);
            }
            if (!_fileMapping.TryGetValue(c.FilePath, out var fileList))
                _fileMapping[c.FilePath] = fileList = new List<int>();
            fileList.Add(i);
        }
    }

    public IndexStats Stats
    {
        get
        {
            var langCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var c in Chunks)
            {
                if (!string.IsNullOrEmpty(c.Language))
                    langCounts[c.Language!] = langCounts.TryGetValue(c.Language!, out var n) ? n + 1 : 1;
            }
            return new IndexStats(
                IndexedFiles: _fileMapping.Count,
                TotalChunks: Chunks.Count,
                Languages: langCounts);
        }
    }

    /// <summary>Create and index a SembleIndex from a directory.</summary>
    /// <exception cref="DirectoryNotFoundException">If <paramref name="path"/> does not exist.</exception>
    /// <exception cref="IOException">If <paramref name="path"/> exists but is not a directory.</exception>
    public static SembleIndex FromPath(
        string path,
        IEncoder? model = null,
        IReadOnlySet<string>? extensions = null,
        IReadOnlySet<string>? ignore = null,
        bool includeTextFiles = false)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
            throw new DirectoryNotFoundException($"Path does not exist: {path}");
        if (!Directory.Exists(path))
            throw new IOException($"Path is not a directory: {path}");

        model ??= Dense.LoadModel();
        var resolved = System.IO.Path.GetFullPath(path);
        var (bm25, semantic, chunks) = Create.CreateIndexFromPath(
            resolved, model, extensions, ignore, includeTextFiles, displayRoot: resolved);
        return new SembleIndex(model, bm25, semantic, chunks);
    }

    /// <summary>Clone a git repository (depth 1) into a temp dir and index it.</summary>
    /// <exception cref="InvalidOperationException">If git is not installed or the clone fails.</exception>
    public static SembleIndex FromGit(
        string url,
        string? @ref = null,
        IEncoder? model = null,
        IReadOnlySet<string>? extensions = null,
        IReadOnlySet<string>? ignore = null,
        bool includeTextFiles = false)
    {
        var tempDir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "semble-clone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            GitRunner.CloneResult result;
            try
            {
                result = GitRunner.CurrentRunner(url, @ref, tempDir);
            }
            catch (GitRunner.GitNotInstalledException ex)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }
            if (result.ExitCode != 0)
                throw new InvalidOperationException(
                    $"git clone failed for '{url}':\n{result.Stderr.Trim()}");

            model ??= Dense.LoadModel();
            var resolved = System.IO.Path.GetFullPath(tempDir);
            var (bm25, semantic, chunks) = Create.CreateIndexFromPath(
                resolved, model, extensions, ignore, includeTextFiles, displayRoot: resolved);
            return new SembleIndex(model, bm25, semantic, chunks);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    public List<SearchResult> Search(
        string query,
        int topK = 10,
        SearchMode mode = SearchMode.Hybrid,
        double? alpha = null,
        IReadOnlyList<string>? filterLanguages = null,
        IReadOnlyList<string>? filterPaths = null)
    {
        if (Chunks.Count == 0 || string.IsNullOrWhiteSpace(query))
            return new List<SearchResult>();

        var selector = GetSelectorVector(filterLanguages, filterPaths);
        return mode switch
        {
            SearchMode.Bm25 => Semble.Search.SearchBm25(query, _bm25, Chunks, topK, selector),
            SearchMode.Semantic => Semble.Search.SearchSemantic(query, Model, _semantic, Chunks, topK, selector),
            SearchMode.Hybrid => Semble.Search.SearchHybrid(
                query, Model, _semantic, _bm25, Chunks, topK, alpha, selector),
            _ => throw new ArgumentException($"Unknown search mode: {mode}", nameof(mode)),
        };
    }

    /// <summary>String-mode overload mirroring the Python `mode: SearchMode | str` API.</summary>
    public List<SearchResult> Search(
        string query,
        string mode,
        int topK = 10,
        double? alpha = null,
        IReadOnlyList<string>? filterLanguages = null,
        IReadOnlyList<string>? filterPaths = null) =>
        Search(query, topK, SearchModeExtensions.ParseMode(mode), alpha, filterLanguages, filterPaths);

    public List<SearchResult> FindRelated(Chunk source, int topK = 5)
    {
        int[]? selector = !string.IsNullOrEmpty(source.Language)
            ? GetSelectorVector(new[] { source.Language! }, null)
            : null;
        var results = Semble.Search.SearchSemantic(
            source.Content, Model, _semantic, Chunks, topK + 1, selector);
        return results.Where(r => !r.Chunk.Equals(source)).Take(topK).ToList();
    }

    public List<SearchResult> FindRelated(SearchResult source, int topK = 5)
        => FindRelated(source.Chunk, topK);

    private int[]? GetSelectorVector(
        IReadOnlyList<string>? filterLanguages,
        IReadOnlyList<string>? filterPaths)
    {
        if ((filterLanguages is null || filterLanguages.Count == 0)
            && (filterPaths is null || filterPaths.Count == 0))
            return null;

        var sel = new HashSet<int>();
        if (filterLanguages is not null)
        {
            foreach (var lang in filterLanguages)
                if (_languageMapping.TryGetValue(lang, out var list))
                    foreach (var i in list) sel.Add(i);
        }
        if (filterPaths is not null)
        {
            foreach (var p in filterPaths)
                if (_fileMapping.TryGetValue(p, out var list))
                    foreach (var i in list) sel.Add(i);
        }
        return sel.Count == 0 ? null : sel.OrderBy(i => i).ToArray();
    }
}
