namespace NSkyLabs.ProtonDrive.Sync;

/// <summary>Result of a local scan.</summary>
/// <param name="ExcludedPaths">Folders that could not be listed and entries that are not synced (symbolic links,
/// special files, names that are not portable). Nothing at or under these paths is changed on either side: an
/// unreadable folder's contents are unknown, and a remote item must not replace a skipped local entry.</param>
internal sealed record LocalSnapshot(
    IReadOnlyDictionary<string, LocalItem> Items,
    IReadOnlyList<string> ExcludedPaths,
    IReadOnlyList<string> Warnings);

/// <summary>Lists the local sync folder: names, sizes and modification times (no content is read).</summary>
internal static class LocalScanner
{
    public static LocalSnapshot Scan(string root, IgnoreRules ignoreRules)
    {
        var items = new Dictionary<string, LocalItem>(StringComparer.Ordinal);
        var excluded = new List<string>();
        var warnings = new List<string>();

        var pending = new Stack<(DirectoryInfo Directory, string Path)>();
        pending.Push((new DirectoryInfo(root), string.Empty));

        while (pending.TryPop(out var current))
        {
            List<FileSystemInfo> entries;
            try
            {
                entries = current.Directory.EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                excluded.Add(current.Path);
                warnings.Add($"Cannot read {current.Directory.FullName}: {ex.Message}. Leaving it unchanged.");
                continue;
            }

            foreach (var entry in entries)
            {
                var name = entry.Name;
                if (current.Path.Length == 0 && name == SyncPaths.EngineFolderName)
                {
                    continue;
                }

                if (ignoreRules.IsIgnored(name))
                {
                    continue;
                }

                var path = SyncPaths.Combine(current.Path, name);

                if (entry.LinkTarget is not null)
                {
                    excluded.Add(path);
                    warnings.Add($"Skipping {path}: symbolic links are not synced");
                    continue;
                }

                if (!SyncPaths.IsPortableName(name))
                {
                    excluded.Add(path);
                    warnings.Add($"Skipping {path}: the name is not portable");
                    continue;
                }

                switch (entry)
                {
                    case DirectoryInfo directory:
                        items[path] = new LocalItem(path, IsFolder: true, Size: 0, ModifiedTicks: 0);
                        pending.Push((directory, path));
                        break;

                    case FileInfo file when (file.Attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) == 0:
                        items[path] = new LocalItem(path, IsFolder: false, file.Length, file.LastWriteTimeUtc.Ticks);
                        break;

                    default:
                        excluded.Add(path);
                        warnings.Add($"Skipping {path}: not a regular file or folder");
                        break;
                }
            }
        }

        return new LocalSnapshot(items, excluded, warnings);
    }

    /// <summary>Reads one item's current state, or <see langword="null"/> if it no longer exists.</summary>
    public static LocalItem? Stat(string root, string path)
    {
        var fullPath = SyncPaths.ToLocalPath(root, path);

        if (Directory.Exists(fullPath))
        {
            return new LocalItem(path, IsFolder: true, Size: 0, ModifiedTicks: 0);
        }

        var file = new FileInfo(fullPath);
        return file.Exists ? new LocalItem(path, IsFolder: false, file.Length, file.LastWriteTimeUtc.Ticks) : null;
    }
}
