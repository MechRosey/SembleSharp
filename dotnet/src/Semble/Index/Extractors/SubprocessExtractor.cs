using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Semble.Index.Extractors;

/// <summary>
/// Common scaffolding for shell-out extractors: PATH-based binary discovery,
/// availability cache, and a child-process invocation that writes the file's
/// path on argv and reads UTF-8 text from stdout. The binary is run with
/// no shell interpretation (<see cref="ProcessStartInfo.ArgumentList"/>),
/// no stdin, captured stderr, and a configurable timeout.
/// </summary>
internal abstract class SubprocessExtractor : ITextExtractor
{
    public abstract string Name { get; }

    /// <summary>The executable to look up on PATH (e.g. "pdftotext", "markitdown").</summary>
    protected abstract string ExecutableName { get; }

    /// <summary>Argv to pass to the executable, with <c>{path}</c> token replaced by the file path.</summary>
    protected abstract IReadOnlyList<string> BuildArguments(string filePath);

    /// <summary>Language hint for the extracted text (drives the post-extract chunker dispatch).</summary>
    protected abstract string OutputLanguage { get; }

    /// <summary>Maximum wall-clock time before the child is killed and the call fails.</summary>
    protected virtual TimeSpan Timeout => TimeSpan.FromSeconds(60);

    private bool? _availabilityCache;

    public bool IsAvailable => _availabilityCache ??= ProbeAvailability();

    private bool ProbeAvailability()
    {
        // Most extractor binaries respond to --version or --help with exit 0.
        // Catch every binary-not-found shape we might see on the host OS.
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ExecutableName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi);
            if (p is null)
                return false;
            p.StandardInput.Close();
            // Drain so the child can exit.
            _ = p.StandardOutput.ReadToEnd();
            _ = p.StandardError.ReadToEnd();
            return p.WaitForExit((int)Timeout.TotalMilliseconds);
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Hook for concrete extractors to attach metadata (e.g. page breaks) to
    /// the captured stdout. The default implementation just wraps the text
    /// with the declared <see cref="OutputLanguage"/>.
    /// </summary>
    protected virtual ExtractedText PostProcess(string stdout) =>
        new ExtractedText(stdout, OutputLanguage);

    public ExtractedText ExtractText(string filePath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExecutableName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in BuildArguments(filePath))
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"{Name}: Process.Start returned null");

        // Read stdout/stderr concurrently so a wide stderr stream can't
        // backpressure the child into wedging on a full pipe.
        p.StandardInput.Close();
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        if (!p.WaitForExit((int)Timeout.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException(
                $"{Name}: timed out after {Timeout.TotalSeconds:0}s extracting '{filePath}'");
        }

        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();

        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{Name}: exit code {p.ExitCode} on '{filePath}': {stderr.Trim()}");
        }

        return PostProcess(stdout);
    }
}
