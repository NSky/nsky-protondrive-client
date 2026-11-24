using System.Text.RegularExpressions;

namespace NSkyLabs.ProtonDrive.Core.Drive;

/// <summary>
/// Turns remote Drive names into names that are safe to create on the local file system.
/// Remote names come from the server and must never be able to escape the download folder.
/// </summary>
internal static partial class LocalNames
{
    /// <summary>
    /// Returns a single path segment for <paramref name="remoteName"/>, replacing characters the local
    /// OS cannot store (always '/', NUL; on Windows also <c>&lt;&gt;:"|?*\</c>, control characters,
    /// reserved device names and trailing dots/spaces) with '_'.
    /// </summary>
    public static string ToLocalName(string remoteName)
    {
        var name = OperatingSystem.IsWindows()
            ? WindowsInvalidCharacters().Replace(remoteName, "_")
            : remoteName.Replace('/', '_').Replace('\0', '_');

        if (OperatingSystem.IsWindows())
        {
            name = name.TrimEnd('.', ' ');
            if (WindowsReservedName().IsMatch(name))
            {
                name = "_" + name;
            }
        }

        return name switch
        {
            "" => "_",
            "." => "_",
            ".." => "__",
            _ => name,
        };
    }

    /// <summary>Throws if <paramref name="path"/> is not strictly inside <paramref name="root"/>.</summary>
    public static void EnsureInside(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));

        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException($"Refusing to write outside the download folder: {path}");
        }
    }

    [GeneratedRegex(@"[\x00-\x1f<>:""|?*\\/]")]
    private static partial Regex WindowsInvalidCharacters();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsReservedName();
}
