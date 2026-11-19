using System.Text.Json;

namespace NskyProtonDrive.Cli.Auth;

/// <summary>
/// Persists the session to a JSON file readable only by the current user.
/// </summary>
/// <remarks>
/// TODO: move secrets (tokens, key passphrases) to the OS secret store (libsecret / Keychain / Credential Manager).
/// </remarks>
internal sealed class SessionStore(string directory)
{
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly Lock _lock = new();

    public string FilePath { get; } = Path.Combine(directory, "session.json");

    public StoredSession? Load()
    {
        lock (_lock)
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            using var stream = File.OpenRead(FilePath);
            return JsonSerializer.Deserialize(stream, StoredSessionJsonContext.Default.StoredSession);
        }
    }

    public void Save(StoredSession session)
    {
        lock (_lock)
        {
            var directory = Path.GetDirectoryName(FilePath)!;
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(directory, PrivateDirectoryMode);
            }

            var tempPath = FilePath + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = PrivateFileMode;
            }

            using (var stream = new FileStream(tempPath, options))
            {
                JsonSerializer.Serialize(stream, session, StoredSessionJsonContext.Default.StoredSession);
            }

            File.Move(tempPath, FilePath, overwrite: true);
        }
    }

    public bool Delete()
    {
        lock (_lock)
        {
            if (!File.Exists(FilePath))
            {
                return false;
            }

            File.Delete(FilePath);
            return true;
        }
    }
}
