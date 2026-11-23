using System.Diagnostics;
using System.Globalization;

namespace NskyProtonDrive.Cli.Terminal;

/// <summary>
/// Thread-safe progress for a batch of file transfers, rendered on stderr.
/// On a terminal it redraws one status line; when stderr is redirected it prints one line per finished file.
/// </summary>
internal sealed class TransferProgress : IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

    private readonly bool _enabled;
    private readonly bool _interactive;
    private readonly string _verb;
    private readonly long _totalBytes;
    private readonly int _totalFiles;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly Lock _consoleLock = new();
    private readonly CancellationTokenSource _stopRendering = new();
    private readonly Task _renderLoop;

    private long _transferredBytes;
    private int _completedFiles;
    private string _currentName = string.Empty;
    private bool _lineVisible;

    public TransferProgress(string verb, int totalFiles, long totalBytes, bool enabled)
    {
        _verb = verb;
        _totalFiles = totalFiles;
        _totalBytes = totalBytes;
        _enabled = enabled;
        _interactive = enabled && !Console.IsErrorRedirected;
        _renderLoop = _interactive ? RenderLoopAsync(_stopRendering.Token) : Task.CompletedTask;
    }

    public long TransferredBytes => Interlocked.Read(ref _transferredBytes);

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public void AddBytes(long delta) => Interlocked.Add(ref _transferredBytes, delta);

    public void FileStarted(string displayName) => Volatile.Write(ref _currentName, displayName);

    public void FileCompleted(string displayName)
    {
        Interlocked.Increment(ref _completedFiles);

        if (_enabled && !_interactive)
        {
            WriteLine($"{_verb} {displayName}");
        }
    }

    /// <summary>Writes a message without corrupting the status line. Always shown, even with progress disabled.</summary>
    public void WriteLine(string message)
    {
        lock (_consoleLock)
        {
            ClearLine();
            Console.Error.WriteLine(message);
            if (_interactive)
            {
                DrawLine();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopRendering.CancelAsync().ConfigureAwait(false);
        await _renderLoop.ConfigureAwait(false);

        lock (_consoleLock)
        {
            ClearLine();
        }

        _stopRendering.Dispose();
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    public static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private async Task RenderLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        try
        {
            do
            {
                lock (_consoleLock)
                {
                    DrawLine();
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    private void DrawLine()
    {
        var transferred = TransferredBytes;
        var elapsed = _stopwatch.Elapsed.TotalSeconds;
        var rate = elapsed > 0.5 ? transferred / elapsed : 0;

        var percent = _totalBytes > 0 ? Math.Min(100, transferred * 100 / _totalBytes) : 0;
        var eta = rate > 0 && _totalBytes > transferred
            ? " ETA " + FormatDuration(TimeSpan.FromSeconds((_totalBytes - transferred) / rate))
            : string.Empty;

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{percent,3}% {FormatBytes(transferred)}/{FormatBytes(_totalBytes)}  {Volatile.Read(ref _completedFiles)}/{_totalFiles} files  {FormatBytes((long)rate)}/s{eta}  {Volatile.Read(ref _currentName)}");

        var width = GetConsoleWidth();
        if (line.Length > width)
        {
            line = line[..Math.Max(0, width - 1)] + "…";
        }

        Console.Error.Write("\r\u001b[2K" + line);
        _lineVisible = true;
    }

    private void ClearLine()
    {
        if (_lineVisible)
        {
            Console.Error.Write("\r\u001b[2K");
            _lineVisible = false;
        }
    }

    private static int GetConsoleWidth()
    {
        try
        {
            var width = Console.WindowWidth;
            return width > 10 ? width - 1 : 79;
        }
        catch (IOException)
        {
            return 79;
        }
    }
}
