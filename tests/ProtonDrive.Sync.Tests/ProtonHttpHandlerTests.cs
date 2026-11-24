using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using NSkyLabs.ProtonDrive.Core.Auth;

namespace NSkyLabs.ProtonDrive.Sync.Tests;

/// <summary>
/// Two clients sharing one session file refresh at the same time against a fake server that, like Proton's,
/// accepts each refresh token only once. Both must end up with working tokens.
/// </summary>
public sealed class ProtonHttpHandlerTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("protondrive-tests-").FullName;

    [Fact]
    public async Task Concurrent_refreshes_with_the_same_token_reach_the_server_once()
    {
        var store = new SessionStore(_directory);
        store.Save(NewSession("refresh-0"));

        var server = new FakeAuthServer("refresh-0");

        // Separate handlers and stores stand for separate processes; they only share the file and its lock.
        var clientA = CreateClient(new SessionStore(_directory), server);
        var clientB = CreateClient(new SessionStore(_directory), server);

        var responses = await Task.WhenAll(RefreshAsync(clientA, "refresh-0"), RefreshAsync(clientB, "refresh-0"));

        Assert.Equal(1, server.RefreshCount);
        Assert.All(responses, r => Assert.Equal(1000, r["Code"]!.GetValue<int>()));
        Assert.Equal(responses[0]["RefreshToken"]!.GetValue<string>(), responses[1]["RefreshToken"]!.GetValue<string>());
        Assert.Equal(server.CurrentRefreshToken, store.Load()!.RefreshToken);
    }

    [Fact]
    public async Task A_refresh_with_the_current_token_is_sent_and_saved()
    {
        var store = new SessionStore(_directory);
        store.Save(NewSession("refresh-0"));
        var server = new FakeAuthServer("refresh-0");

        var response = await RefreshAsync(CreateClient(store, server), "refresh-0");
        var second = await RefreshAsync(CreateClient(store, server), response["RefreshToken"]!.GetValue<string>());

        Assert.Equal(2, server.RefreshCount);
        Assert.Equal(1000, second["Code"]!.GetValue<int>());
        Assert.Equal(server.CurrentRefreshToken, store.Load()!.RefreshToken);
        Assert.Equal(second["AccessToken"]!.GetValue<string>(), store.Load()!.AccessToken);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private static HttpClient CreateClient(SessionStore store, FakeAuthServer server)
    {
        return new HttpClient(new ProtonHttpHandler(store) { InnerHandler = server }) { BaseAddress = new Uri("https://drive-api.example/") };
    }

    private static async Task<JsonNode> RefreshAsync(HttpClient client, string refreshToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "auth/v4/refresh")
        {
            Content = new StringContent($$"""{"RefreshToken":"{{refreshToken}}","ResponseType":"token","GrantType":"refresh_token"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-pm-uid", "session-1");

        using var response = await client.SendAsync(request);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

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

    /// <summary>Issues new tokens for the current refresh token and rejects any other, as Proton does.</summary>
    private sealed class FakeAuthServer(string initialRefreshToken) : HttpMessageHandler
    {
        private readonly Lock _lock = new();
        private int _generation;

        public string CurrentRefreshToken { get; private set; } = initialRefreshToken;

        public int RefreshCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var sent = body["RefreshToken"]!.GetValue<string>();

            // Give a concurrent request time to race this one.
            await Task.Delay(100, cancellationToken);

            lock (_lock)
            {
                if (sent != CurrentRefreshToken)
                {
                    return Json(HttpStatusCode.BadRequest, """{"Code":10013,"Error":"Invalid refresh token"}""");
                }

                _generation++;
                RefreshCount++;
                CurrentRefreshToken = $"refresh-{_generation}";

                return Json(
                    HttpStatusCode.OK,
                    $$"""{"Code":1000,"AccessToken":"access-{{_generation}}","RefreshToken":"{{CurrentRefreshToken}}","TokenType":"Bearer","Scopes":["full"],"UID":"session-1"}""");
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
