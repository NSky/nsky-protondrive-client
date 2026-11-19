using System.Collections.Concurrent;
using Proton.Sdk.Caching;

namespace NskyProtonDrive.Cli.Auth;

/// <summary>
/// In-memory cache that also records the account key passphrases the SDK derives during login,
/// so they can be persisted and restored when the session is resumed.
/// </summary>
/// <remarks>
/// Relies on the key format used by <c>Proton.Drive.Sdk.Account.Caching.SessionSecretCache</c>.
/// </remarks>
internal sealed class PassphraseCapturingCacheRepository : ICacheRepository
{
    private const string PassphraseKeyPrefix = "account:passphrase:";

    private readonly ICacheRepository _inner = new InMemoryCacheRepository();
    private readonly ConcurrentDictionary<string, byte[]> _passphrases = new();

    public IReadOnlyDictionary<string, byte[]> CapturedPassphrases => _passphrases;

    public ValueTask EnsureValueFormatVersionAsync(string valueFormatVersion, CancellationToken cancellationToken)
        => _inner.EnsureValueFormatVersionAsync(valueFormatVersion, cancellationToken);

    public ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
    {
        if (key.StartsWith(PassphraseKeyPrefix, StringComparison.Ordinal))
        {
            _passphrases[key[PassphraseKeyPrefix.Length..]] = value.ToArray();
        }

        return _inner.SetAsync(key, value, cancellationToken);
    }

    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken)
    {
        if (key.StartsWith(PassphraseKeyPrefix, StringComparison.Ordinal))
        {
            _passphrases.TryRemove(key[PassphraseKeyPrefix.Length..], out _);
        }

        return _inner.RemoveAsync(key, cancellationToken);
    }

    public ValueTask ClearAsync()
    {
        _passphrases.Clear();
        return _inner.ClearAsync();
    }

    public ValueTask<byte[]?> TryGetAsync(string key, CancellationToken cancellationToken)
        => _inner.TryGetAsync(key, cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
