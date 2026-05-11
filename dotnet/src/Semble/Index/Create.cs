using System.Text;

namespace Semble.Index;

/// <summary>Mirrors src/semble/index/create.py.</summary>
public static class Create
{
    private static readonly Encoding LenientUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>
    /// Build BM25 + dense indexes from files under <paramref name="path"/>.
    /// Throws <see cref="InvalidOperationException"/> when no indexable files are found
    /// (mirrors the Python ValueError contract).
    /// </summary>
    public static (Bm25 Bm25, DenseBackend Semantic, List<Chunk> Chunks) CreateIndexFromPath(
        string path,
        IEncoder model,
        IReadOnlySet<string>? extensions = null,
        IReadOnlySet<string>? ignore = null,
        bool includeTextFiles = false,
        string? displayRoot = null,
        DateTime? excludeNewerThan = null)
    {
        var resolvedExtensions = FileWalker.FilterExtensions(extensions, includeTextFiles);

        var chunks = new List<Chunk>();
        foreach (var filePath in FileWalker.WalkFiles(path, resolvedExtensions, ignore, excludeNewerThan))
        {
            if (new FileInfo(filePath).Length > 1_000_000)
                continue;

            var language = FileWalker.LanguageForPath(filePath);
            string source;
            try
            {
                source = File.ReadAllText(filePath, LenientUtf8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            string chunkPath = displayRoot is null
                ? filePath
                : System.IO.Path.GetRelativePath(displayRoot, filePath).Replace('\\', '/');

            chunks.AddRange(Chunker.ChunkSource(source, chunkPath, language));
        }

        if (chunks.Count == 0)
            throw new InvalidOperationException($"No supported files found under {path}.");

        var embeddings = Dense.EmbedChunks(model, chunks);
        var bm25 = new Bm25();
        var corpus = chunks
            .Select(c => (IReadOnlyList<string>)Tokens.Tokenize(Sparse.EnrichForBm25(c)))
            .ToList();
        bm25.Index(corpus);
        var semantic = new DenseBackend(embeddings);
        return (bm25, semantic, chunks);
    }
}
