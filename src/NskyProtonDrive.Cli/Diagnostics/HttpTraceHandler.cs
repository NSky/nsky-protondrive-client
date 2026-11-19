using System.Diagnostics;

namespace NskyProtonDrive.Cli.Diagnostics;

/// <summary>
/// Writes one line per HTTP request (method, URL, status, duration) to stderr.
/// Enabled with the <c>PROTONDRIVE_DEBUG_HTTP=1</c> environment variable. Never logs headers or bodies.
/// </summary>
internal sealed class HttpTraceHandler : DelegatingHandler
{
    private static readonly HashSet<string> SecretHeaders = new(StringComparer.OrdinalIgnoreCase) { "Authorization", "x-pm-uid", "Cookie" };

    public static bool IsEnabled { get; } = Environment.GetEnvironmentVariable("PROTONDRIVE_DEBUG_HTTP") is "1" or "2" or "true";

    /// <summary>Level 2 also prints request headers (credentials redacted).</summary>
    private static bool IncludeHeaders { get; } = Environment.GetEnvironmentVariable("PROTONDRIVE_DEBUG_HTTP") is "2";

    public static Func<DelegatingHandler>? FactoryIfEnabled => IsEnabled ? () => new HttpTraceHandler() : null;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (IncludeHeaders)
        {
            foreach (var (name, values) in request.Headers)
            {
                var shown = SecretHeaders.Contains(name) ? "<redacted>" : string.Join(" | ", values);
                Console.Error.WriteLine($"[http]   {name}: {shown}");
            }
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            Console.Error.WriteLine($"[http] {request.Method} {request.RequestUri} -> {(int)response.StatusCode} ({stopwatch.ElapsedMilliseconds} ms)");
            return response;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[http] {request.Method} {request.RequestUri} -> {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }
}
