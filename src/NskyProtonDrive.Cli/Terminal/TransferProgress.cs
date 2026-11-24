using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace NskyProtonDrive.Cli.Terminal;

/// <summary>
/// Thread-safe progress for a batch of file transfers, rendered on stderr.
/// On a terminal it redraws a block with one line per file in progress and a summary line below;
/// when stderr is redirected it prints one line per finished file.
/// </summary>
internal sealed class TransferProgress : IAsyncDisposable
{
    private const int MaxFileLines = 8;
    private const string ClearToEndOfScreen = "\u001b[J";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

    private readonly bool _enabled;
    private readonly bool _interactive;
    private readonly string _verb;
    private readonly long _totalBytes;
    private readonly int _totalFiles;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly Lock _consoleLock = new();
    private readonly List<FileTransfer> _active = [];
    private readonly CancellationTokenSource _stopRendering = new();
    private readonly Task _renderLoop;

    private long _transferredBytes;
    private int _completedFiles;
    private int _drawnLines;

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

    /// <summary>Registers a file whose transfer is starting; report its progress through the returned handle.</summary>
    public FileTransfer StartFile(string displayName, long size)
    {
        var transfer = new FileTransfer(this, displayName, size);
        lock (_consoleLock)
        {
            _active.Add(transfer);
        }

        return transfer;
    }

    /// <summary>
    /// Removes a file from the display. A failed file's bytes are taken back out of the total, so the summary
    /// only counts what actually arrived.
    /// </summary>
    public void FinishFile(FileTransfer transfer, bool succeeded)
    {
        if (succeeded)
        {
            Interlocked.Increment(ref _completedFiles);
        }
        else
        {
            transfer.Rollback();
        }

        lock (_consoleLock)
        {
            _active.Remove(transfer);
        }

        if (succeeded && _enabled && !_interactive)
        {
            WriteLine($"{_verb} {transfer.Name}");
        }
    }

    /// <summary>Writes a message above the progress block. Always shown, even with progress disabled.</summary>
    public void WriteLine(string message)
    {
        lock (_consoleLock)
        {
            var output = new StringBuilder();
            AppendClear(output);
            output.Append(message).Append('\n');
            if (_interactive)
            {
                AppendBlock(output);
            }

            Console.Error.Write(output.ToString());
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopRendering.CancelAsync().ConfigureAwait(false);
        await _renderLoop.ConfigureAwait(false);

        lock (_consoleLock)
        {
            var output = new StringBuilder();
            AppendClear(output);
            Console.Error.Write(output.ToString());
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

    private void AddBytes(long delta) => Interlocked.Add(ref _transferredBytes, delta);

    private async Task RenderLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        try
        {
            do
            {
                lock (_consoleLock)
                {
                    var output = new StringBuilder();
                    AppendClear(output);
                    AppendBlock(output);
                    Console.Error.Write(output.ToString());
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    /// <summary>Moves the cursor back to the first line of the block and erases everything from there down.</summary>
    private void AppendClear(StringBuilder output)
    {
        if (_drawnLines == 0)
        {
            return;
        }

        output.Append('\r');
        if (_drawnLines > 1)
        {
            output.Append(CultureInfo.InvariantCulture, $"\u001b[{_drawnLines - 1}A");
        }

        output.Append(ClearToEndOfScreen);
        _drawnLines = 0;
    }

    /// <summary>
    /// Appends the block without a trailing newline, so the cursor stays on its last line. Every line is cut to the
    /// terminal width: a wrapped line would take two rows and the next redraw would leave a copy behind.
    /// </summary>
    private void AppendBlock(StringBuilder output)
    {
        var width = GetConsoleWidth();
        var lines = new List<string>(MaxFileLines + 2);

        var shown = _active.Take(MaxFileLines).ToList();
        if (shown.Count > 0)
        {
            var sizeColumn = shown.Max(f => FormatFileBytes(f).Length);
            var nameColumn = Math.Max(1, Math.Min(shown.Max(f => f.Name.Length), width - sizeColumn - 9));

            foreach (var file in shown)
            {
                var size = FormatFileBytes(file);
                lines.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{file.Percent,4}%  {ShortenStart(file.Name, nameColumn).PadRight(nameColumn)}  {size.PadLeft(sizeColumn)}"));
            }

            if (_active.Count > shown.Count)
            {
                lines.Add($"       ... and {_active.Count - shown.Count} more");
            }
        }

        lines.Add(FormatSummary());

        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                output.Append('\n');
            }

            output.Append(Truncate(lines[i], width));
        }

        _drawnLines = lines.Count;
    }

    private string FormatSummary()
    {
        var transferred = TransferredBytes;
        var elapsed = _stopwatch.Elapsed.TotalSeconds;
        var rate = elapsed > 0.5 ? transferred / elapsed : 0;

        var percent = _totalBytes > 0 ? Math.Clamp(transferred * 100 / _totalBytes, 0, 100) : 0;
        var eta = rate > 0 && _totalBytes > transferred
            ? " ETA " + FormatDuration(TimeSpan.FromSeconds((_totalBytes - transferred) / rate))
            : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{percent,4}%  {FormatBytes(transferred)}/{FormatBytes(_totalBytes)}  {Volatile.Read(ref _completedFiles)}/{_totalFiles} files  {FormatBytes((long)rate)}/s{eta}");
    }

    private static string FormatFileBytes(FileTransfer file) => $"{FormatBytes(file.Bytes)} / {FormatBytes(file.Size)}";

    /// <summary>Keeps the end of a path, which holds the file name.</summary>
    private static string ShortenStart(string text, int width)
    {
        return text.Length <= width ? text : "…" + text[^Math.Max(0, width - 1)..];
    }

    private static string Truncate(string line, int width)
    {
        return line.Length <= width ? line : line[..Math.Max(0, width - 1)] + "…";
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

    /// <summary>One file in progress. <see cref="Report"/> takes the cumulative byte count for the file.</summary>
    public sealed class FileTransfer
    {
        private readonly TransferProgress _owner;
        private long _bytes;

        internal FileTransfer(TransferProgress owner, string name, long size)
        {
            _owner = owner;
            Name = name;
            Size = size;
        }

        public string Name { get; }

        public long Size { get; }

        internal long Bytes => Interlocked.Read(ref _bytes);

        internal long Percent => Size > 0 ? Math.Clamp(Bytes * 100 / Size, 0, 100) : 0;

        /// <summary>Sets how many bytes of this file have been transferred so far (it may go down after a retry).</summary>
        public void Report(long cumulativeBytes)
        {
            _owner.AddBytes(cumulativeBytes - Interlocked.Exchange(ref _bytes, cumulativeBytes));
        }

        internal void Rollback()
        {
            _owner.AddBytes(-Interlocked.Exchange(ref _bytes, 0));
        }
    }
}
