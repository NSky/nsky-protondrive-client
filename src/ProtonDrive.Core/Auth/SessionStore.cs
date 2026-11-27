using System.Security.Cryptography;
using System.Text.Json;
using NSkyLabs.ProtonDrive.Core.IO;

namespace NSkyLabs.ProtonDrive.Core.Auth;

/// <summary>
/// Persists the session in a file readable only by the current user: plain JSON (<c>session.json</c>), or encrypted
/// with systemd-creds (<c>session.cred</c>) once the user turned that on with <c>protondrive session encrypt</c>.
/// </summary>
/// <remarks>
/// <para>
/// Several processes can use the session at once (a sync service and commands run by hand), so every read and
/// write happens under a cross-process lock (<c>session.lock</c>). Token refreshes are coordinated through the same
/// lock by <see cref="ProtonHttpHandler"/>.
/// </para>
/// <para>
/// Whether <c>session.cred</c> exists is the setting: while it does, every save is encrypted and a leftover
/// <c>session.json</c> is removed. Logging out leaves it empty rather than deleting it, so the next login is
/// encrypted too. Decrypting is slow (about 1.5 seconds), so the decrypted contents are kept in memory and reused
/// while the encrypted file is unchanged (compared by hash: every encryption gives different bytes).
/// </para>
/// </remarks>
internal sealed class SessionStore(string directory, ISessionProtector? protector = null)
{
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(60);

    private readonly ISessionProtector _protector = protector ?? SystemdCredentialProtector.Instance;

    private (byte[] CiphertextHash, byte[] Plaintext)? _decrypted;

    public string FilePath { get; } = Path.Combine(directory, "session.json");

    public string EncryptedFilePath { get; } = Path.Combine(directory, "session.cred");

    private string LockPath { get; } = Path.Combine(directory, "session.lock");

    /// <summary>Whether sessions are saved encrypted (<c>session.cred</c>).</summary>
    public bool IsEncrypted => File.Exists(EncryptedFilePath);

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

    /// <summary>Removes the saved session (it does not need to be decrypted). Encryption stays on.</summary>
    public bool Delete()
    {
        using (AcquireLock())
        {
            var existed = File.Exists(FilePath) || HasEncryptedSession();
            File.Delete(FilePath);
            if (IsEncrypted)
            {
                WriteAtomically(EncryptedFilePath, []);
            }

            _decrypted = null;
            return existed;
        }
    }

    /// <summary>
    /// Turns encryption on or off and converts the saved session, if there is one. Turning it on checks that the
    /// session can be decrypted again before the plain file is removed.
    /// </summary>
    /// <returns>False when it already was in that state.</returns>
    public bool SetEncrypted(bool encrypted)
    {
        using (AcquireLock())
        {
            if (encrypted == IsEncrypted)
            {
                return false;
            }

            var session = LoadWhileLocked();
            if (encrypted)
            {
                var plaintext = session is null ? [] : Serialize(session);
                var ciphertext = _protector.Protect(plaintext);
                if (!_protector.Unprotect(ciphertext).AsSpan().SequenceEqual(plaintext))
                {
                    throw new SessionProtectionException("The encrypted session did not decrypt to the same contents; it was left unencrypted.");
                }

                WriteAtomically(EncryptedFilePath, session is null ? [] : ciphertext);
                File.Delete(FilePath);
                _decrypted = session is null ? null : (SHA256.HashData(ciphertext), plaintext);
            }
            else
            {
                if (session is not null)
                {
                    WriteAtomically(FilePath, Serialize(session));
                }

                File.Delete(EncryptedFilePath);
                _decrypted = null;
            }

            return true;
        }
    }

    /// <summary>Reads the file; the caller must hold the lock.</summary>
    internal StoredSession? LoadWhileLocked()
    {
        if (IsEncrypted)
        {
            var ciphertext = File.ReadAllBytes(EncryptedFilePath);
            if (ciphertext.Length == 0)
            {
                return null;
            }

            var hash = SHA256.HashData(ciphertext);
            if (_decrypted is not { } cached || !cached.CiphertextHash.AsSpan().SequenceEqual(hash))
            {
                cached = (hash, _protector.Unprotect(ciphertext));
                _decrypted = cached;
            }

            // A new object every time: callers change the one they hold.
            return Deserialize(cached.Plaintext);
        }

        if (!File.Exists(FilePath))
        {
            return null;
        }

        return Deserialize(File.ReadAllBytes(FilePath));
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
        var plaintext = Serialize(session);
        if (!IsEncrypted)
        {
            WriteAtomically(FilePath, plaintext);
            return;
        }

        var ciphertext = _protector.Protect(plaintext);
        WriteAtomically(EncryptedFilePath, ciphertext);
        _decrypted = (SHA256.HashData(ciphertext), plaintext);

        // Left over if a switch to encryption was interrupted.
        File.Delete(FilePath);
    }

    private bool HasEncryptedSession() => new FileInfo(EncryptedFilePath) is { Exists: true, Length: > 0 };

    private static byte[] Serialize(StoredSession session) => JsonSerializer.SerializeToUtf8Bytes(session, StoredSessionJsonContext.Default.StoredSession);

    private static StoredSession? Deserialize(byte[] json) => JsonSerializer.Deserialize(json, StoredSessionJsonContext.Default.StoredSession);

    private static void WriteAtomically(string path, byte[] contents)
    {
        PrivateDirectory.Ensure(Path.GetDirectoryName(path)!);

        // A new file every time: an existing one (left by a crash) would keep whatever permissions it had.
        var tempPath = path + ".tmp";
        File.Delete(tempPath);

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = PrivateFileMode;
        }

        using (var stream = new FileStream(tempPath, options))
        {
            stream.Write(contents);
        }

        File.Move(tempPath, path, overwrite: true);
    }
}
