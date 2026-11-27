using System.Diagnostics;
using System.ComponentModel;

namespace NSkyLabs.ProtonDrive.Cli.Service;

/// <summary>
/// Notices when the running program has been replaced on disk (a new version was installed) so <c>sync --watch</c>
/// can restart on it once no sync pass is running.
/// </summary>
/// <remarks>
/// Installing replaces the executable (install.sh removes and copies the folder, pacman renames over it), so the
/// running process either sees its <c>/proc/self/exe</c> marked "(deleted)" or a different size or modification time.
/// The new version counts as ready only once it has stopped changing for a while and runs: an install copies several
/// files, and restarting halfway through would start an incomplete program.
/// </remarks>
internal sealed class UpdateWatcher
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SettlePeriod = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(20);

    private readonly string _path;
    private readonly (long Size, DateTime Modified) _started;
    private (long Size, DateTime Modified)? _lastSeen;
    private DateTime _lastChange;
    private DateTime _nextCheck;

    private UpdateWatcher(string path, (long, DateTime) started)
    {
        _path = path;
        _started = started;
    }

    /// <summary>The version the new program reports, once <see cref="IsUpdateReady"/> returned true.</summary>
    public string? NewVersion { get; private set; }

    /// <summary>Null when the program's own file cannot be found (e.g. run with <c>dotnet run</c> from a build).</summary>
    public static UpdateWatcher? Create()
    {
        var path = Environment.ProcessPath;
        if (path is null || Stat(path) is not { } stat || Path.GetFileNameWithoutExtension(path) == "dotnet")
        {
            return null;
        }

        return new UpdateWatcher(path, stat);
    }

    /// <summary>Cheap to call often: looks at the file at most every 30 seconds.</summary>
    public bool IsUpdateReady()
    {
        var now = DateTime.UtcNow;
        if (NewVersion is not null)
        {
            return true;
        }

        if (now < _nextCheck)
        {
            return false;
        }

        _nextCheck = now + (_lastSeen is null ? CheckInterval : TimeSpan.FromSeconds(2));

        var current = Stat(_path);
        if (current is null)
        {
            // Removed, or halfway through being replaced: keep running what is loaded.
            _lastSeen = null;
            return false;
        }

        if (current == _started && !IsDeleted())
        {
            return false;
        }

        if (current != _lastSeen)
        {
            _lastSeen = current;
            _lastChange = now;
            return false;
        }

        if (now - _lastChange < SettlePeriod)
        {
            return false;
        }

        NewVersion = RunVersion();
        if (NewVersion is null)
        {
            // Not runnable yet (or broken): look again later rather than restarting into it.
            _lastChange = now;
            _nextCheck = now + CheckInterval;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Asks the systemd user manager to restart the unit this process runs in, so it starts the new version (with
    /// unit files reloaded, in case the install changed them). False when not run by systemd or the request failed.
    /// </summary>
    public static bool RequestServiceRestart()
    {
        if (Environment.GetEnvironmentVariable("INVOCATION_ID") is not { Length: > 0 } || GetOwnUserUnit() is not { } unit)
        {
            return false;
        }

        Run("systemctl", "--user", "daemon-reload");
        return Run("systemctl", "--user", "--no-block", "restart", unit) == 0;
    }

    /// <summary>The user service this process belongs to, from its cgroup (".../user@1000.service/app.slice/x.service").</summary>
    private static string? GetOwnUserUnit()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/cgroup"))
            {
                var path = line[(line.LastIndexOf(':') + 1)..];
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 2
                    && segments[^1].EndsWith(".service", StringComparison.Ordinal)
                    && segments.Any(s => s.StartsWith("user@", StringComparison.Ordinal))
                    && !segments[^1].StartsWith("user@", StringComparison.Ordinal))
                {
                    return segments[^1];
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static (long Size, DateTime Modified)? Stat(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsDeleted()
    {
        try
        {
            return new FileInfo("/proc/self/exe").LinkTarget?.EndsWith(" (deleted)", StringComparison.Ordinal) == true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string? RunVersion()
    {
        try
        {
            var startInfo = new ProcessStartInfo(_path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            startInfo.ArgumentList.Add("version");
            using var process = Process.Start(startInfo)!;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(VersionTimeout))
            {
                process.Kill();
                return null;
            }

            return process.ExitCode == 0 && output.Result.Trim() is { Length: > 0 } version ? version : null;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static int Run(string fileName, params string[] args)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo)!;
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            return -1;
        }
    }
}
