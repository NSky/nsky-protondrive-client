using System.Security.Cryptography;
using System.Text;
using NSkyLabs.ProtonDrive.Core.Auth;

namespace NSkyLabs.ProtonDrive.Sync.Tests;

/// <summary>Saving the session as plain JSON or encrypted, and switching between the two.</summary>
public sealed class SessionStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("protondrive-tests-").FullName;
    private readonly FakeProtector _protector = new();

    [Fact]
    public void Encrypting_replaces_the_plain_file_and_keeps_the_session()
    {
        var store = NewStore();
        store.Save(NewSession("refresh-0"));

        Assert.True(store.SetEncrypted(true));

        Assert.False(File.Exists(store.FilePath));
        Assert.DoesNotContain("refresh-0", File.ReadAllText(store.EncryptedFilePath));
        Assert.Equal("refresh-0", NewStore().Load()!.RefreshToken);
    }

    [Fact]
    public void Decrypting_restores_the_plain_file()
    {
        var store = NewStore();
        store.Save(NewSession("refresh-0"));
        store.SetEncrypted(true);

        Assert.True(store.SetEncrypted(false));

        Assert.False(store.IsEncrypted);
        Assert.Contains("refresh-0", File.ReadAllText(store.FilePath));
        Assert.Equal("refresh-0", NewStore().Load()!.RefreshToken);
    }

    [Fact]
    public void Encryption_stays_on_after_logout_so_the_next_login_is_encrypted()
    {
        var store = NewStore();
        store.SetEncrypted(true);
        store.Save(NewSession("refresh-0"));

        Assert.True(store.Delete());
        Assert.Null(store.Load());
        Assert.True(store.IsEncrypted);

        store.Save(NewSession("refresh-1"));
        Assert.False(File.Exists(store.FilePath));
        Assert.Equal("refresh-1", NewStore().Load()!.RefreshToken);
    }

    [Fact]
    public void An_unchanged_encrypted_file_is_decrypted_only_once()
    {
        var store = NewStore();
        store.SetEncrypted(true);
        store.Save(NewSession("refresh-0"));
        var reader = NewStore();
        var before = _protector.Decryptions;

        reader.Load();
        reader.Load();
        reader.UpdateTokens("session-1", "access-0", "refresh-0");

        Assert.Equal(before + 1, _protector.Decryptions);
    }

    [Fact]
    public void A_change_by_another_process_is_seen()
    {
        var reader = NewStore();
        reader.SetEncrypted(true);
        reader.Save(NewSession("refresh-0"));
        Assert.Equal("refresh-0", reader.Load()!.RefreshToken);

        // Same length and likely the same modification time: only the contents differ.
        NewStore().UpdateTokens("session-1", "access-1", "refresh-1");

        Assert.Equal("refresh-1", reader.Load()!.RefreshToken);
    }

    [Fact]
    public void Each_load_returns_a_separate_object()
    {
        var store = NewStore();
        store.SetEncrypted(true);
        store.Save(NewSession("refresh-0"));

        // AuthService changes the session it holds and then saves through UpdateTokens, which compares with a load.
        var held = store.Load()!;
        held.RefreshToken = "refresh-1";
        store.UpdateTokens(held.SessionId, held.AccessToken, held.RefreshToken);

        Assert.Equal("refresh-1", NewStore().Load()!.RefreshToken);
    }

    [Fact]
    public void A_failed_encryption_leaves_the_plain_session_in_place()
    {
        var store = NewStore();
        store.Save(NewSession("refresh-0"));
        _protector.Fail = true;

        Assert.Throws<SessionProtectionException>(() => store.SetEncrypted(true));

        Assert.False(store.IsEncrypted);
        Assert.Equal("refresh-0", store.Load()!.RefreshToken);
    }

    [Fact]
    public void A_leftover_temporary_file_does_not_keep_its_permissions()
    {
        var store = NewStore();
        File.WriteAllText(store.FilePath + ".tmp", "old");
        File.SetUnixFileMode(store.FilePath + ".tmp", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        store.Save(NewSession("refresh-0"));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.FilePath));
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private SessionStore NewStore() => new(_directory, _protector);

    private static StoredSession NewSession(string refreshToken) => new()
    {
        SessionId = "session-1",
        Username = "user",
        UserId = "user-1",
        AccessToken = "access-0",
        RefreshToken = refreshToken,
        Scopes = ["full"],
        PasswordMode = 1,
        KeyPassphrases = [],
    };

    /// <summary>Stands in for systemd-creds: a random prefix (so every encryption differs) and reversible scrambling.</summary>
    private sealed class FakeProtector : ISessionProtector
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FAKE");

        public bool Fail { get; set; }

        public int Decryptions { get; private set; }

        public byte[] Protect(byte[] plaintext)
        {
            if (Fail)
            {
                throw new SessionProtectionException("encryption failed");
            }

            return [.. Magic, .. RandomNumberGenerator.GetBytes(8), .. plaintext.Select(b => (byte)(b ^ 0x5A))];
        }

        public byte[] Unprotect(byte[] ciphertext)
        {
            Decryptions++;
            Assert.True(ciphertext.AsSpan(0, 4).SequenceEqual(Magic));
            return [.. ciphertext.Skip(12).Select(b => (byte)(b ^ 0x5A))];
        }
    }
}
