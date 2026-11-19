using NskyProtonDrive.Cli.Diagnostics;
using NskyProtonDrive.Cli.Terminal;
using Proton.Drive.Sdk.Account;
using Proton.Drive.Sdk.Account.Authentication;
using Proton.Drive.Sdk.Account.Users;
using Proton.Sdk.Api;

namespace NskyProtonDrive.Cli.Auth;

internal sealed class NotLoggedInException() : Exception($"Not logged in. Run '{AppInfo.CommandName} login' first.");

/// <summary>
/// A resumed, ready-to-use Proton session plus the account client bound to it.
/// </summary>
internal sealed record AuthenticatedContext(ProtonApiSession Session, ProtonAccountClient AccountClient, StoredSession Stored);

internal sealed class AuthService(SessionStore store)
{
    private const int MaxSecondFactorAttempts = 3;

    public async Task<AuthenticatedContext> LoginAsync(string? username, CancellationToken cancellationToken)
    {
        Console.Error.WriteLine(AppInfo.ThirdPartyNotice);
        Console.Error.WriteLine();

        username ??= ConsolePrompt.ReadRequiredLine("Username: ");
        var password = ConsolePrompt.ReadSecret("Password: ");

        try
        {
            var cache = new PassphraseCapturingCacheRepository();
            var options = new ProtonSessionOptions
            {
                CacheRepository = cache,
                UserAgent = AppInfo.UserAgent,
                CustomHttpMessageHandlerFactory = HttpTraceHandler.FactoryIfEnabled,
            };

            var session = await ProtonApiSession.BeginAsync(username, password, AppInfo.ProtonAppVersion, options, cancellationToken)
                .ConfigureAwait(false);

            var neededSecondFactor = session.IsWaitingForSecondFactorCode;
            if (neededSecondFactor)
            {
                await ApplySecondFactorAsync(session, cancellationToken).ConfigureAwait(false);
            }

            // BeginAsync only derives key passphrases itself for single-password accounts without 2FA.
            if (session.PasswordMode == PasswordMode.Dual)
            {
                var mailboxPassword = ConsolePrompt.ReadSecret("Mailbox password: ");
                try
                {
                    await session.ApplyDataPasswordAsync(mailboxPassword, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Array.Clear(mailboxPassword);
                }
            }
            else if (neededSecondFactor || cache.CapturedPassphrases.Count == 0)
            {
                await session.ApplyDataPasswordAsync(password, cancellationToken).ConfigureAwait(false);
            }

            var accountClient = CreateAccountClient(session);

            // Unlocking the primary address key proves the derived passphrases are correct.
            await VerifyKeysAsync(accountClient, cancellationToken).ConfigureAwait(false);

            var (accessToken, refreshToken) = await session.TokenCredential.GetTokensAsync(cancellationToken).ConfigureAwait(false);

            var stored = new StoredSession
            {
                SessionId = session.SessionId.ToString(),
                Username = session.Username,
                UserId = session.UserId.ToString(),
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                Scopes = session.Scopes,
                PasswordMode = (int)session.PasswordMode,
                KeyPassphrases = cache.CapturedPassphrases.ToDictionary(x => x.Key, x => Convert.ToBase64String(x.Value)),
            };

            store.Save(stored);
            AttachPersistence(session, stored);

            return new AuthenticatedContext(session, accountClient, stored);
        }
        finally
        {
            Array.Clear(password);
        }
    }

    public async Task<AuthenticatedContext> ResumeAsync(CancellationToken cancellationToken)
    {
        var stored = store.Load() ?? throw new NotLoggedInException();

        var options = new ProtonClientOptions
        {
            UserAgent = AppInfo.UserAgent,
            CustomHttpMessageHandlerFactory = HttpTraceHandler.FactoryIfEnabled,
        };

        var session = ProtonApiSession.Resume(
            (SessionId)stored.SessionId,
            stored.Username,
            (UserId)stored.UserId,
            stored.AccessToken,
            stored.RefreshToken,
            stored.Scopes,
            isWaitingForSecondFactorCode: false,
            (PasswordMode)stored.PasswordMode,
            accountBaseUrl: null,
            AppInfo.ProtonAppVersion,
            options);

        foreach (var (keyId, passphrase) in stored.KeyPassphrases)
        {
            await session.SetAccountKeyPassphraseAsync(keyId, Convert.FromBase64String(passphrase), cancellationToken).ConfigureAwait(false);
        }

        AttachPersistence(session, stored);

        return new AuthenticatedContext(session, CreateAccountClient(session), stored);
    }

    public async Task<bool> LogoutAsync(CancellationToken cancellationToken)
    {
        if (store.Load() is null)
        {
            return false;
        }

        try
        {
            var context = await ResumeAsync(cancellationToken).ConfigureAwait(false);
            await context.Session.EndAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ProtonApiException or HttpRequestException)
        {
            Console.Error.WriteLine($"Warning: could not end the session on the server ({ex.Message}). Removing local session anyway.");
        }

        store.Delete();
        return true;
    }

    private static ProtonAccountClient CreateAccountClient(ProtonApiSession session)
    {
        return new ProtonAccountClient(
            session.CreateHttpClientFactory(),
            session.ClientConfiguration.CacheRepository,
            session.SessionSecretCache,
            session.ClientConfiguration.Telemetry);
    }

    private static async Task ApplySecondFactorAsync(ProtonApiSession session, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var code = ConsolePrompt.ReadRequiredLine("Two-factor code (TOTP or recovery code): ");
            try
            {
                await session.ApplySecondFactorCodeAsync(code, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (ProtonApiException ex) when (attempt < MaxSecondFactorAttempts)
            {
                Console.Error.WriteLine($"Two-factor code rejected: {ex.Message}");
            }
        }
    }

    private static async Task VerifyKeysAsync(ProtonAccountClient accountClient, CancellationToken cancellationToken)
    {
        try
        {
            var address = await accountClient.GetCurrentUserDefaultAddressAsync(cancellationToken).ConfigureAwait(false);
            await accountClient.GetAddressPrimaryPrivateKeyAsync(address.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not HttpRequestException)
        {
            throw new InvalidOperationException(
                "Authenticated, but the account keys could not be unlocked. Check the (mailbox) password.",
                ex);
        }
    }

    private void AttachPersistence(ProtonApiSession session, StoredSession stored)
    {
        session.TokenCredential.TokensRefreshed += (accessToken, refreshToken) =>
        {
            stored.AccessToken = accessToken;
            stored.RefreshToken = refreshToken;
            store.Save(stored);
        };

        session.Ended += () => store.Delete();
    }
}
