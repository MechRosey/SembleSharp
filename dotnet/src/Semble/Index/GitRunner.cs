using System.ComponentModel;
using System.Diagnostics;

namespace Semble.Index;

/// <summary>
/// Wraps the `git clone` invocation behind a swappable delegate so tests can
/// replicate the Python `subprocess.run` mock scenarios (e.g. "git is not
/// installed").
/// </summary>
public static class GitRunner
{
    public sealed record CloneResult(int ExitCode, string Stderr);

    /// <summary>Sentinel thrown by the runner when git is not on PATH.</summary>
    public sealed class GitNotInstalledException : Exception
    {
        public GitNotInstalledException(Exception? inner = null)
            : base("git is not installed or not on PATH", inner) { }
    }

    public delegate CloneResult Runner(string url, string? @ref, string targetDir);

    /// <summary>Test seam: replace to mock the git invocation.</summary>
    public static Runner CurrentRunner { get; set; } = DefaultRunner;

    /// <summary>Milliseconds before a clone is killed. Enforced by DefaultRunner.</summary>
    public static int CloneTimeoutMs { get; set; } = 60_000;

    public static CloneResult DefaultRunner(string url, string? @ref, string targetDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("clone");
        psi.ArgumentList.Add("--depth");
        psi.ArgumentList.Add("1");
        if (@ref is not null)
        {
            psi.ArgumentList.Add("--branch");
            psi.ArgumentList.Add(@ref);
        }
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(url);
        psi.ArgumentList.Add(targetDir);

        Process p;
        try
        {
            p = Process.Start(psi)!;
        }
        catch (Win32Exception ex)
        {
            throw new GitNotInstalledException(ex);
        }
        catch (FileNotFoundException ex)
        {
            throw new GitNotInstalledException(ex);
        }

        p.StandardInput.Close();
        var stderrTask = Task.Run(() => p.StandardError.ReadToEnd());
        if (!p.WaitForExit(CloneTimeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return new CloneResult(-1, $"git clone timed out after {CloneTimeoutMs / 1000} s");
        }
        return new CloneResult(p.ExitCode, stderrTask.GetAwaiter().GetResult());
    }
}
