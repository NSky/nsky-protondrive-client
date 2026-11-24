using System.Text.Json;
using NSkyLabs.ProtonDrive.Core.IO;

namespace NSkyLabs.ProtonDrive.Core.Auth;

/// <summary>
/// Persists the session to a JSON file readable only by the current user.
/// </summary>
/// <remarks>
/// Several processes can use the session at once (a sync service and commands run by hand), so every read and
/// write happens under a cross-process lock (<c>session.lock</c>). Token refreshes are coordinated through the same
/// lock by <see cref="ProtonHttpHandler"/>.
/// TODO: move secrets (tokens, key passphrases) to the OS secret store (libsecret / Keychain / Credential Manager).
/// </remarks>
internal sealed class SessionStore(string directory)
{
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(60);

    public string FilePath { get; } = Path.Combine(directory, "session.json");

    private string LockPath { get; } = Path.Combine(directory, "session.lock");

    public FileLock AcquireLock() => FileLock.Acquire(LockPath, LockTimeout);

    public Task<FileLock> AcquireLockAsync(CancellationToken cancellationToken) => FileLock.AcquireAsync(LockPath, LockTimeout, cancellationToken);

    public StoredSession? Load()
    {
        using (AcquireLock())
        {
            return LoadWhileLocked();
        }
    }

    public void Save(StoredSession session)
    {
        using (AcquireLock())
        {
            SaveWhileLocked(session);
        }
    }

    /// <summary>Records refreshed tokens, unless the file now belongs to a different session (e.g. a new login).</summary>
    public void UpdateTokens(string sessionId, string accessToken, string refreshToken)
    {
        using (AcquireLock())
        {
            UpdateTokensWhileLocked(sessionId, accessToken, refreshToken);
        }
    }

    public bool Delete()
    {
        using (AcquireLock())
        {
            if (!File.Exists(FilePath))
            {
                return false;
            }

            File.Delete(FilePath);
            return true;
        }
    }

    /// <summary>Reads the file; the caller must hold the lock.</summary>
    internal StoredSession? LoadWhileLocked()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        using var stream = File.OpenRead(FilePath);
        return JsonSerializer.Deserialize(stream, StoredSessionJsonContext.Default.StoredSession);
    }

    internal void UpdateTokensWhileLocked(string sessionId, string accessToken, string refreshToken)
    {
        var stored = LoadWhileLocked();
        if (stored is null || stored.SessionId != sessionId)
        {
            return;
        }

        if (stored.AccessToken == accessToken && stored.RefreshToken == refreshToken)
        {
            return;
        }

        stored.AccessToken = accessToken;
        stored.RefreshToken = refreshToken;
        SaveWhileLocked(stored);
    }

    /// <summary>Writes the file atomically; the caller must hold the lock.</summary>
    internal void SaveWhileLocked(StoredSession session)
    {
        PrivateDirectory.Ensure(Path.GetDirectoryName(FilePath)!);

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
