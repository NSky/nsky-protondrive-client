using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NSkyLabs.ProtonDrive.Core.Diagnostics;

namespace NSkyLabs.ProtonDrive.Core.Auth;

/// <summary>
/// HTTP handler added to every Proton API client. It traces requests when enabled and makes token refreshes safe
/// when several processes share one session.
/// </summary>
/// <remarks>
/// Proton refresh tokens are single-use. If two processes refresh with the same refresh token, the second request
/// fails and its session would be lost. So a refresh (<c>POST auth/v4/refresh</c>) runs under the session file lock:
/// <list type="bullet">
/// <item>If <c>session.json</c> already holds a newer refresh token than the one being sent, another process has
/// refreshed in the meantime. The request is not sent; the handler answers it with the stored tokens.</item>
/// <item>Otherwise the request is sent, and the new tokens are written to <c>session.json</c> before the lock is
/// released, so the next process to refresh sees them.</item>
/// </list>
/// </remarks>
internal sealed class ProtonHttpHandler(SessionStore store) : DelegatingHandler
{
    private const string RefreshPath = "auth/v4/refresh";
    private const string SessionIdHeader = "x-pm-uid";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post
            || request.RequestUri?.AbsolutePath.EndsWith(RefreshPath, StringComparison.Ordinal) != true
            || !request.Headers.TryGetValues(SessionIdHeader, out var sessionIdValues)
            || sessionIdValues.FirstOrDefault() is not { } sessionId)
        {
            return await HttpTrace.SendAsync(request, base.SendAsync, cancellationToken).ConfigureAwait(false);
        }

        return await RefreshAsync(request, sessionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> RefreshAsync(HttpRequestMessage request, string sessionId, CancellationToken cancellationToken)
    {
        using var sessionLock = await store.AcquireLockAsync(cancellationToken).ConfigureAwait(false);

        var sentRefreshToken = await ReadRefreshTokenAsync(request, cancellationToken).ConfigureAwait(false);
        var stored = store.LoadWhileLocked();

        if (stored is not null
            && stored.SessionId == sessionId
            && sentRefreshToken is not null
            && stored.RefreshToken != sentRefreshToken)
        {
            HttpTrace.Note($"{request.Method} {request.RequestUri} answered with tokens refreshed by another process");
            return CreateRefreshResponse(request, stored);
        }

        var response = await HttpTrace.SendAsync(request, base.SendAsync, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content is null)
        {
            return response;
        }

        // Buffer the body so it can be read here and again by the SDK.
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var replacement = new ByteArrayContent(body);
        foreach (var (name, values) in response.Content.Headers)
        {
            replacement.Headers.TryAddWithoutValidation(name, values);
        }

        response.Content = replacement;

        if (TryReadTokens(body, out var accessToken, out var refreshToken))
        {
            store.UpdateTokensWhileLocked(sessionId, accessToken, refreshToken);
        }

        return response;
    }

    private static async Task<string?> ReadRefreshTokenAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is null)
        {
            return null;
        }

        // Buffer the body so it can still be sent after being read here.
        var body = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var replacement = new ByteArrayContent(body);
        foreach (var (name, values) in request.Content.Headers)
        {
            replacement.Headers.TryAddWithoutValidation(name, values);
        }

        request.Content = replacement;

        try
        {
            return JsonNode.Parse(body)?["RefreshToken"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryReadTokens(byte[] body, out string accessToken, out string refreshToken)
    {
        accessToken = refreshToken = string.Empty;
        try
        {
            var json = JsonNode.Parse(body);
            if (json?["Code"]?.GetValue<int>() != 1000
                || json["AccessToken"]?.GetValue<string>() is not { Length: > 0 } access
                || json["RefreshToken"]?.GetValue<string>() is not { Length: > 0 } refresh)
            {
                return false;
            }

            accessToken = access;
            refreshToken = refresh;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Builds the response the server would have sent, from tokens another process already obtained.</summary>
    private static HttpResponseMessage CreateRefreshResponse(HttpRequestMessage request, StoredSession stored)
    {
        var json = new JsonObject
        {
            ["Code"] = 1000,
            ["AccessToken"] = stored.AccessToken,
            ["TokenType"] = "Bearer",
            ["Scopes"] = new JsonArray(stored.Scopes.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
            ["UID"] = stored.SessionId,
            ["RefreshToken"] = stored.RefreshToken,
        };

        var content = new StringContent(json.ToJsonString(), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content };
    }
}
