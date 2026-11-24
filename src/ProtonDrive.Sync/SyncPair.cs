using System.Text.Json;
using System.Text.Json.Serialization;
using NSkyLabs.ProtonDrive.Core.IO;

namespace NSkyLabs.ProtonDrive.Sync;

/// <summary>A local folder kept in sync with a remote folder.</summary>
internal sealed record SyncPair
{
    /// <summary>Short random ID; names the pair's state database and lock file.</summary>
    public required string Id { get; init; }

    public required string LocalPath { get; init; }

    /// <summary>The remote folder's path when the pair was added (for display; the node UID is authoritative).</summary>
    public required string RemotePath { get; init; }

    public required string RemoteRootUid { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public string DatabasePath => Path.Combine(SyncPairStore.StateDirectory, $"{Id}.db");

    [JsonIgnore]
    public string LockPath => Path.Combine(SyncPairStore.StateDirectory, $"{Id}.lock");
}

/// <summary>Reads and writes the configured sync pairs (<c>~/.config/protondrive/sync.json</c>).</summary>
internal static class SyncPairStore
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    public static string FilePath { get; } = Path.Combine(AppInfo.ConfigDirectory, "sync.json");

    public static string StateDirectory { get; } = Path.Combine(AppInfo.StateDirectory, "sync");

    private static string LockPath => Path.Combine(AppInfo.ConfigDirectory, "sync.lock");

    public static IReadOnlyList<SyncPair> Load()
    {
        using (FileLock.Acquire(LockPath, LockTimeout))
        {
            return LoadCore();
        }
    }

    /// <summary>Loads, changes and saves the pair list under the lock.</summary>
    public static T Update<T>(Func<List<SyncPair>, T> change)
    {
        using (FileLock.Acquire(LockPath, LockTimeout))
        {
            var pairs = LoadCore();
            var result = change(pairs);
            Save(pairs);
            return result;
        }
    }

    /// <summary>Finds a pair by ID or by local folder.</summary>
    public static SyncPair? Find(IEnumerable<SyncPair> pairs, string idOrPath)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(idOrPath));
        return pairs.FirstOrDefault(p => p.Id == idOrPath || p.LocalPath == fullPath);
    }

    private static List<SyncPair> LoadCore()
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        using var stream = File.OpenRead(FilePath);
        var file = JsonSerializer.Deserialize(stream, SyncPairJsonContext.Default.SyncPairFile);
        return file?.Pairs ?? [];
    }

    private static void Save(List<SyncPair> pairs)
    {
        PrivateDirectory.Ensure(AppInfo.ConfigDirectory);

        var tempPath = FilePath + ".tmp";
        using (var stream = File.Create(tempPath))
        {
            JsonSerializer.Serialize(stream, new SyncPairFile { Pairs = pairs }, SyncPairJsonContext.Default.SyncPairFile);
        }

        File.Move(tempPath, FilePath, overwrite: true);
    }
}

internal sealed class SyncPairFile
{
    public List<SyncPair> Pairs { get; init; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SyncPairFile))]
internal sealed partial class SyncPairJsonContext : JsonSerializerContext;
