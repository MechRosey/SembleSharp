namespace Semble.Index;

/// <summary>
/// Downloads a model2vec / potion-code model from HuggingFace Hub into a
/// local directory.  Uses the public HTTPS resolve URL so no API token is
/// needed for public models.
/// </summary>
/// <remarks>
/// Files downloaded: <c>tokenizer.json</c>, <c>model.safetensors</c>,
/// <c>config.json</c>. The set matches what <see cref="Dense.LoadModel"/>
/// expects.  SHA-256 verification against the upstream LFS hash is a
/// planned follow-up; connections are TLS-verified by <see cref="HttpClient"/>.
/// </remarks>
public static class ModelDownloader
{
    private const string HfBase = "https://huggingface.co";

    private static readonly string[] RequiredFiles =
    {
        "tokenizer.json",
        "model.safetensors",
        "config.json",
    };

    /// <summary>
    /// Download <paramref name="modelId"/> (e.g.
    /// <c>"minishlab/potion-code-16M"</c>) into <paramref name="destDir"/>.
    /// Existing files whose size matches the downloaded byte count are skipped.
    /// </summary>
    /// <param name="modelId">HuggingFace repo id in <c>org/model</c> form.</param>
    /// <param name="destDir">Local directory; created if absent.</param>
    /// <param name="progress">
    /// Optional callback invoked periodically with
    /// <c>(filename, bytesReceived, totalBytes)</c>. <c>totalBytes</c> is -1
    /// when the server does not supply Content-Length.
    /// </param>
    public static async Task DownloadAsync(
        string modelId,
        string destDir,
        Action<string, long, long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destDir);

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "semble-dotnet/1.0");

        foreach (var file in RequiredFiles)
        {
            var url = $"{HfBase}/{modelId}/resolve/main/{file}";
            var dest = Path.Combine(destDir, file);

            await DownloadFileAsync(client, url, dest, file, progress, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task DownloadFileAsync(
        HttpClient client,
        string url,
        string dest,
        string displayName,
        Action<string, long, long>? progress,
        CancellationToken ct)
    {
        using var response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? -1L;

        // Skip if an already-complete file exists.
        if (File.Exists(dest) && total > 0 && new FileInfo(dest).Length == total)
        {
            progress?.Invoke(displayName, total, total);
            return;
        }

        var tmp = dest + ".tmp";
        try
        {
            using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var dst = File.Create(tmp);

            var buf = new byte[81920];
            long received = 0;
            int read;
            while ((read = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf, 0, read, ct).ConfigureAwait(false);
                received += read;
                progress?.Invoke(displayName, received, total);
            }
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }

        // Atomic rename.
        if (File.Exists(dest))
            File.Delete(dest);
        File.Move(tmp, dest);
    }
}
