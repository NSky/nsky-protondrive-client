using System.Diagnostics;

namespace NSkyLabs.ProtonDrive.Core.Diagnostics;

/// <summary>
/// Writes one line per HTTP request (method, URL, status, duration) to stderr.
/// Enabled with the <c>PROTONDRIVE_DEBUG_HTTP=1</c> environment variable; <c>=2</c> also prints request headers with
/// credentials redacted. Never logs bodies.
/// </summary>
internal static class HttpTrace
{
    private static readonly HashSet<string> SecretHeaders = new(StringComparer.OrdinalIgnoreCase) { "Authorization", "x-pm-uid", "Cookie" };

    public static bool IsEnabled { get; } = Environment.GetEnvironmentVariable("PROTONDRIVE_DEBUG_HTTP") is "1" or "2" or "true";

    private static bool IncludeHeaders { get; } = Environment.GetEnvironmentVariable("PROTONDRIVE_DEBUG_HTTP") is "2";

    public static async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return await send(request, cancellationToken).ConfigureAwait(false);
        }

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
            var response = await send(request, cancellationToken).ConfigureAwait(false);
            Console.Error.WriteLine($"[http] {request.Method} {request.RequestUri} -> {(int)response.StatusCode} ({stopwatch.ElapsedMilliseconds} ms)");
            return response;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[http] {request.Method} {request.RequestUri} -> {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public static void Note(string message)
    {
        if (IsEnabled)
        {
            Console.Error.WriteLine($"[http] {message}");
        }
    }
}
