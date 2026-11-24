using System.Text.Json.Serialization;

namespace NSkyLabs.ProtonDrive.Core.Auth;

/// <summary>
/// Everything needed to resume an authenticated Proton session without asking for the password again.
/// </summary>
internal sealed record StoredSession
{
    public required string SessionId { get; init; }

    public required string Username { get; init; }

    public required string UserId { get; init; }

    public required string AccessToken { get; set; }

    public required string RefreshToken { get; set; }

    public required IReadOnlyList<string> Scopes { get; init; }

    /// <summary>1 = single password, 2 = separate mailbox (data) password.</summary>
    public required int PasswordMode { get; init; }

    /// <summary>
    /// Passphrases (base64) that unlock the account keys, keyed by key ID. They are derived from the
    /// account/mailbox password and are required to decrypt Drive data.
    /// </summary>
    public required Dictionary<string, string> KeyPassphrases { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StoredSession))]
internal sealed partial class StoredSessionJsonContext : JsonSerializerContext;
