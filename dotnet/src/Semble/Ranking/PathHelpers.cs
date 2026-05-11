namespace Semble.Ranking;

/// <summary>
/// Lexical path helpers that mimic Python pathlib.PurePosixPath semantics.
/// We avoid System.IO.Path here because (a) Python pathlib treats forward slashes
/// uniformly across platforms and (b) GetFileNameWithoutExtension(".gitignore")
/// returns "" in .NET but ".gitignore" in pathlib.
/// </summary>
internal static class PathHelpers
{
    /// <summary>Lower-cased file name (last path segment).</summary>
    public static string Name(string filePath)
    {
        var normalized = Normalize(filePath);
        int lastSlash = normalized.LastIndexOf('/');
        return lastSlash < 0 ? normalized : normalized[(lastSlash + 1)..];
    }

    /// <summary>
    /// File stem matching pathlib.Path.stem: filename minus the final extension.
    /// Leading-dot files (".gitignore") have no extension, so their stem is the
    /// full name. Multi-suffix names ("foo.tar.gz") strip only the last suffix.
    /// </summary>
    public static string Stem(string filePath)
    {
        var name = Name(filePath);
        if (name == "" || name == "." || name == "..")
            return name;
        int firstNonDot = 0;
        while (firstNonDot < name.Length && name[firstNonDot] == '.')
            firstNonDot++;
        if (firstNonDot == name.Length)
            return name;
        int lastDot = name.LastIndexOf('.');
        return lastDot < firstNonDot ? name : name[..lastDot];
    }

    /// <summary>Name of the immediate parent directory, "" when there is none.</summary>
    public static string ParentName(string filePath)
    {
        var normalized = Normalize(filePath);
        int lastSlash = normalized.LastIndexOf('/');
        if (lastSlash < 0)
            return "";
        var parent = normalized[..lastSlash];
        int prevSlash = parent.LastIndexOf('/');
        return prevSlash < 0 ? parent : parent[(prevSlash + 1)..];
    }

    private static string Normalize(string filePath) =>
        filePath.IndexOf('\\') >= 0 ? filePath.Replace('\\', '/') : filePath;
}
