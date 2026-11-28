using System.Globalization;
using System.Text;
using NSkyLabs.ProtonDrive.Core.Diagnostics;

namespace NSkyLabs.ProtonDrive.Cli.Terminal;

/// <summary>
/// Shows <see cref="Activity"/> while a command works, so a long silent step (a big folder scan) visibly progresses.
/// </summary>
/// <remarks>
/// <para>On a terminal: one status line on stderr with a spinner, the current step, its counter, the elapsed time and
/// the item being worked on. It appears only after a step has run for a moment, so quick commands look as before.
/// Console output is routed through a writer that erases the line before anything else is printed, so messages
/// never mix with it. It is not drawn while a line is half-written (a prompt) or while a transfer display is
/// shown, which is a progress display of its own.</para>
/// <para>Without a terminal (logs, the systemd journal): a line every <see cref="HeartbeatInterval"/> while a step
/// runs that long, e.g. "Still listing the remote folder: 120,000 items (2:00)".</para>
/// </remarks>
internal sealed class ActivityStatus : IAsyncDisposable
{
    private static readonly string[] Frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private static readonly TimeSpan ShowAfter = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private static ActivityStatus? _instance;
    private static int _suspended;

    private readonly Lock _lock = new();
    private readonly TextWriter _error;
    private readonly TextWriter _output;
    private readonly bool _interactive;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private bool _drawn;
    private bool _atLineStart = true;
    private int _frame;

    private ActivityStatus(bool interactive)
    {
        _interactive = interactive;
        _error = Console.Error;
        _output = Console.Out;

        if (interactive)
        {
            Console.SetError(new GuardedWriter(this, _error));
            if (!Console.IsOutputRedirected)
            {
                Console.SetOut(new GuardedWriter(this, _output));
            }

            _loop = RenderLoopAsync(_stop.Token);
        }
        else
        {
            _loop = HeartbeatLoopAsync(_stop.Token);
        }
    }

    /// <summary>
    /// Starts showing activity. <paramref name="showLiveLine"/> false (--no-progress, --quiet) turns off the live
    /// line on a terminal; the occasional heartbeat line in logs stays.
    /// </summary>
    public static ActivityStatus? Start(bool showLiveLine)
    {
        var terminal = !Console.IsErrorRedirected && Environment.GetEnvironmentVariable("TERM") != "dumb";
        if (terminal && !showLiveLine)
        {
            return null;
        }

        _instance = new ActivityStatus(interactive: terminal);
        return _instance;
    }

    /// <summary>Hides the line while another live display (transfer progress) owns the bottom of the screen.</summary>
    public static IDisposable Suspend()
    {
        Interlocked.Increment(ref _suspended);
        _instance?.Clear();
        return new Resumer();
    }

    /// <summary>The user ended a line of input (the terminal echoed the newline, which no writer saw).</summary>
    public static void InputLineEnded()
    {
        if (_instance is { } status)
        {
            lock (status._lock)
            {
                status._atLineStart = true;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);

        if (_interactive)
        {
            Clear();
            Console.SetError(_error);
            Console.SetOut(_output);
        }

        _instance = null;
        _stop.Dispose();
    }

    private void Clear()
    {
        lock (_lock)
        {
            ClearLocked();
        }
    }

    private void ClearLocked()
    {
        if (_drawn)
        {
            TerminalOutput.WriteTrusted(_error, "\r\u001b[K");
            _error.Flush();
            _drawn = false;
        }
    }

    private async Task RenderLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (_lock)
                {
                    var step = Activity.Current;
                    if (step is null || Volatile.Read(ref _suspended) > 0 || !_atLineStart || step.Root.Elapsed < ShowAfter)
                    {
                        ClearLocked();
                        continue;
                    }

                    _frame = (_frame + 1) % Frames.Length;
                    var line = Frames[_frame] + " " + TerminalOutput.Name(Describe(step, GetConsoleWidth() - 2));
                    TerminalOutput.WriteTrusted(_error, "\r" + line + "\u001b[K");
                    _error.Flush();
                    _drawn = true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        ActivityStep? reported = null;
        var nextReport = TimeSpan.Zero;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var step = Activity.Current;
                if (step is null)
                {
                    continue;
                }

                if (!ReferenceEquals(step, reported))
                {
                    reported = step;
                    nextReport = HeartbeatInterval;
                }

                if (step.Elapsed >= nextReport)
                {
                    nextReport += HeartbeatInterval;
                    _error.WriteLine("Still " + LowerFirst(Describe(step, int.MaxValue)));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    /// <summary>"Listing the remote folder: 12,345 items · 0:42 · Photos/2019", cut to <paramref name="width"/>.</summary>
    internal static string Describe(ActivityStep step, int width)
    {
        var text = new StringBuilder(step.Description);

        if (step.Unit is not null)
        {
            var count = step.Count;
            var total = step.Total;
            text.Append(total >= 0
                ? string.Create(CultureInfo.InvariantCulture, $": {count:N0}/{total:N0} {step.Unit}")
                : string.Create(CultureInfo.InvariantCulture, $": {count:N0} {step.Unit}"));
        }

        text.Append(" (").Append(TransferProgress.FormatDuration(step.Elapsed)).Append(')');

        if (step.Detail is { Length: > 0 } detail)
        {
            var room = width - text.Length - 3;
            if (room >= 12)
            {
                text.Append(" · ").Append(detail.Length <= room ? detail : "…" + detail[^(room - 1)..]);
            }
        }

        return text.Length <= width ? text.ToString() : text.ToString(0, Math.Max(0, width - 1)) + "…";
    }

    private static string LowerFirst(string text) => text.Length > 0 ? char.ToLowerInvariant(text[0]) + text[1..] : text;

    private static int GetConsoleWidth()
    {
        try
        {
            var width = Console.WindowWidth;
            return width > 20 ? width - 1 : 79;
        }
        catch (IOException)
        {
            return 79;
        }
    }

    private sealed class Resumer : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref _suspended);
            }
        }
    }

    /// <summary>Erases the status line before passing text on, and notes whether the cursor is at a line start.</summary>
    private sealed class GuardedWriter(ActivityStatus owner, TextWriter inner) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override IFormatProvider FormatProvider => inner.FormatProvider;

        public override string NewLine
        {
#pragma warning disable CS8765 // Matches TextWriter.NewLine, whose setter accepts null.
            get => inner.NewLine;
            set => inner.NewLine = value;
#pragma warning restore CS8765
        }

        public override void Write(char value) => Write(value.ToString());

        public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            lock (owner._lock)
            {
                owner.ClearLocked();
                inner.Write(value);
                inner.Flush();
                owner._atLineStart = value[^1] == '\n';
            }
        }

        public override void WriteLine(string? value) => Write(value + inner.NewLine);

        public override void WriteLine() => Write(inner.NewLine);

        public override void Flush() => inner.Flush();
    }
}
