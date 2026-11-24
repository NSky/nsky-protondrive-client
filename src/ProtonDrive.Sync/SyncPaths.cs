using System.Text.RegularExpressions;

namespace NSkyLabs.ProtonDrive.Sync;

/// <summary>Helpers for sync-relative paths ('/'-separated, no leading slash, "" is the root).</summary>
internal static class SyncPaths
{
    /// <summary>Folder at the root of every local sync folder that holds the engine's temporary files and trash.</summary>
    public const string EngineFolderName = ".protondrive";

    public const string PartialSuffix = ".protondrive-part";

    public static string Combine(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;

    public static string GetParent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    public static string GetName(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>True if <paramref name="path"/> is <paramref name="ancestor"/> or inside it.</summary>
    public static bool IsSameOrUnder(string path, string ancestor)
    {
        return ancestor.Length == 0
            || path == ancestor
            || (path.Length > ancestor.Length && path[ancestor.Length] == '/' && path.StartsWith(ancestor, StringComparison.Ordinal));
    }

    /// <summary>True if <paramref name="path"/> is strictly inside <paramref name="ancestor"/>.</summary>
    public static bool IsUnder(string path, string ancestor) => path != ancestor && IsSameOrUnder(path, ancestor);

    /// <summary>Replaces the <paramref name="from"/> prefix of <paramref name="path"/> with <paramref name="to"/>.</summary>
    public static string Rebase(string path, string from, string to) => path == from ? to : to + path[from.Length..];

    public static int Depth(string path) => path.Length == 0 ? 0 : path.Count(c => c == '/') + 1;

    public static string ToLocalPath(string root, string path) => path.Length == 0 ? root : Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// A name that can be used unchanged on both sides. Names that would need changing locally are not synced
    /// (and reported), so a path always means the same item on both sides.
    /// </summary>
    public static bool IsPortableName(string name)
    {
        return name.Length > 0
            && name is not "." and not ".."
            && name.IndexOf('/') < 0
            && name.IndexOf('\0') < 0
            && LocalNames.ToLocalName(name) == name;
    }

    /// <summary>Builds "name (conflict host 2026-01-31 154500).ext", the name a conflicting local copy is renamed to.</summary>
    public static string GetConflictName(string name, string host, DateTimeOffset time)
    {
        var extension = Path.GetExtension(name);
        var stem = extension.Length > 0 && extension.Length < name.Length ? name[..^extension.Length] : name;
        if (stem.Length == name.Length)
        {
            extension = string.Empty;
        }

        return $"{stem} (conflict {host} {time.LocalDateTime:yyyy-MM-dd HHmmss}){extension}";
    }
}

/// <summary>File name patterns that are never synced in either direction (editor lock and swap files, OS metadata).</summary>
internal sealed class IgnoreRules
{
    public static readonly IReadOnlyList<string> DefaultPatterns =
    [
        "*" + SyncPaths.PartialSuffix,
        ".~lock.*#",
        "~$*",
        ".*.swp",
        ".*.swx",
        ".DS_Store",
        "Thumbs.db",
        "desktop.ini",
    ];

    private readonly Regex[] _patterns;

    public IgnoreRules(IEnumerable<string> patterns)
    {
        _patterns = patterns.Select(ToRegex).ToArray();
    }

    public static IgnoreRules Default { get; } = new(DefaultPatterns);

    /// <summary>True if an item with this name is not synced. The engine folder at the root is checked separately.</summary>
    public bool IsIgnored(string name) => _patterns.Any(p => p.IsMatch(name));

    private static Regex ToRegex(string glob)
    {
        var pattern = "^" + Regex.Escape(glob).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$";
        return new Regex(pattern, RegexOptions.CultureInvariant);
    }
}
