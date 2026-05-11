using System.Text;
using System.Text.RegularExpressions;

namespace Semble.Index;

/// <summary>
/// Minimal .gitignore pattern matcher. Supports the subset of git's gitignore
/// syntax that Semble's walker needs: literal names, '*' / '**' globs, leading
/// '!' negations, trailing '/' for dir-only, and leading '/' or interior '/'
/// for repository-root anchoring. The full cascade ("everything under a matched
/// directory is matched too") emerges from the walker pruning matched dirs
/// before descending.
/// </summary>
public sealed class GitIgnoreMatcher
{
    private readonly List<Rule> _rules = new();

    private sealed record Rule(Regex Pattern, bool IsNegation, bool DirOnly);

    public GitIgnoreMatcher(IEnumerable<string> lines)
    {
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r', '\n');
            // git treats trailing whitespace as significant only when escaped.
            // We strip plain trailing whitespace; it doesn't affect Semble's tests.
            line = line.TrimEnd();
            if (line.Length == 0 || line[0] == '#')
                continue;

            bool negation = false;
            if (line[0] == '!')
            {
                negation = true;
                line = line[1..];
            }

            bool dirOnly = false;
            if (line.EndsWith('/'))
            {
                dirOnly = true;
                line = line[..^1];
            }

            bool anchored = false;
            if (line.StartsWith('/'))
            {
                anchored = true;
                line = line[1..];
            }
            else if (line.Contains('/'))
            {
                anchored = true;
            }

            string body = GlobToRegex(line);
            string pattern = anchored ? "^" + body + "$" : "(^|.*/)" + body + "$";
            _rules.Add(new Rule(new Regex(pattern, RegexOptions.Compiled), negation, dirOnly));
        }
    }

    /// <summary>
    /// Test whether <paramref name="relativePath"/> is ignored by these rules.
    /// Append a trailing '/' to indicate a directory.
    /// </summary>
    public bool IsIgnored(string relativePath)
    {
        var path = relativePath;
        bool isDir = path.EndsWith('/');
        if (isDir)
            path = path[..^1];
        path = path.TrimStart('/');

        bool ignored = false;
        foreach (var rule in _rules)
        {
            if (rule.DirOnly && !isDir)
                continue;
            if (rule.Pattern.IsMatch(path))
                ignored = !rule.IsNegation;
        }
        return ignored;
    }

    private static string GlobToRegex(string p)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < p.Length; i++)
        {
            char c = p[i];
            if (c == '*')
            {
                if (i + 1 < p.Length && p[i + 1] == '*')
                {
                    if (i + 2 < p.Length && p[i + 2] == '/')
                    {
                        // **/  zero or more directory components
                        sb.Append("(.*/)?");
                        i += 2;
                    }
                    else
                    {
                        sb.Append(".*");
                        i++;
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else if ("\\.+()[]{}|^$".IndexOf(c) >= 0)
            {
                sb.Append('\\').Append(c);
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
