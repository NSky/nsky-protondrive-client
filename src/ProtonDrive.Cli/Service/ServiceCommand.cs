using System.Diagnostics;
using System.ComponentModel;
using NSkyLabs.ProtonDrive.Sync;

namespace NSkyLabs.ProtonDrive.Cli.Service;

internal sealed class ServiceException(string message) : Exception(message);

/// <summary>
/// <c>service enable|disable|status</c>: runs the sync in the background as a systemd <em>user</em> service.
/// </summary>
/// <remarks>
/// The units are installed system-wide (e.g. <c>/usr/local/lib/systemd/user</c>), but each user enables them for
/// themselves, so every user runs their own instance with their own login and folders. Enabling also turns on
/// "linger" for the user, so their systemd instance starts at boot and keeps running after they log out.
/// </remarks>
internal static class ServiceCommand
{
    public const string WatchUnit = "protondrive-sync-watch.service";
    public const string TimerUnit = "protondrive-sync.timer";
    private const string TimerServiceUnit = "protondrive-sync.service";

    public const string Usage = """
        Usage:
          protondrive service enable [--timer]   Sync in the background: continuously (default), or every 15 minutes
                                                 with --timer. Keeps running after you log out.
          protondrive service disable            Stop syncing in the background
          protondrive service status             Show the background sync and its recent log
        """;

    public static int Run(string[] args, SessionStore sessionStore)
    {
        var flags = args.Where(a => a.StartsWith('-')).ToHashSet(StringComparer.Ordinal);
        var positional = args.Where(a => !a.StartsWith('-')).ToArray();

        if (flags.Except(["--timer"]).FirstOrDefault() is { } unknown)
        {
            throw new ServiceException($"Unknown option '{unknown}'.");
        }

        if (positional.Length != 1)
        {
            throw new ServiceException("Expected one of: enable, disable, status.");
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new ServiceException("Background sync uses systemd and is only available on Linux.");
        }

        EnsureUserManager();

        return positional[0] switch
        {
            "enable" => Enable(useTimer: flags.Contains("--timer"), sessionStore),
            "disable" => Disable(),
            "status" => Status(),
            _ => throw new ServiceException($"Unknown service command '{positional[0]}'."),
        };
    }

    private static int Enable(bool useTimer, SessionStore sessionStore)
    {
        if (Environment.UserName == "root" || Environment.GetEnvironmentVariable("SUDO_USER") is { Length: > 0 })
        {
            throw new ServiceException("Run this as the user whose folders should be synced, not with sudo or as root.");
        }

        if (sessionStore.Load() is null)
        {
            throw new ServiceException($"Not logged in. Run '{AppInfo.CommandName} login' first.");
        }

        if (SyncPairStore.Load().Count == 0)
        {
            Console.Error.WriteLine($"Note: no folders are synced yet. Add one with '{AppInfo.CommandName} sync add <local-folder> <remote-folder>'; the service picks it up when it next starts.");
        }

        var unit = useTimer ? TimerUnit : WatchUnit;
        var other = useTimer ? WatchUnit : TimerUnit;

        Systemctl("daemon-reload");
        if (Run("systemctl", "--user", "cat", unit).ExitCode != 0)
        {
            throw new ServiceException($"The systemd unit {unit} is not installed. Install protondrive with 'sudo make install' or the release's install.sh.");
        }

        // Only one mode at a time: the timer and the watch service would do the same work.
        Run("systemctl", "--user", "disable", "--now", other);
        Systemctl("enable", "--now", unit);
        if (!useTimer)
        {
            // Restart so an already running instance picks up a new version or new folders.
            Systemctl("restart", unit);
        }

        Console.WriteLine(useTimer
            ? "Background sync enabled: every 15 minutes."
            : "Background sync enabled: continuously.");

        EnableLinger();

        Console.WriteLine($"Status: '{AppInfo.CommandName} service status'. Log: journalctl --user -u {(useTimer ? TimerServiceUnit : WatchUnit)} -f");
        return 0;
    }

    private static int Disable()
    {
        Run("systemctl", "--user", "disable", "--now", WatchUnit);
        Run("systemctl", "--user", "disable", "--now", TimerUnit);
        Console.WriteLine("Background sync disabled.");

        if (IsLingerEnabled())
        {
            Console.WriteLine("Your systemd user services still keep running after logout (linger). If nothing else needs that, turn it off with: loginctl disable-linger");
        }

        return 0;
    }

    private static int Status()
    {
        var watch = Run("systemctl", "--user", "is-enabled", WatchUnit).Output.Trim();
        var timer = Run("systemctl", "--user", "is-enabled", TimerUnit).Output.Trim();
        var mode = watch == "enabled" ? "continuous (watch)" : timer == "enabled" ? "every 15 minutes (timer)" : "disabled";

        Console.WriteLine($"Background sync: {mode}");
        Console.WriteLine($"Runs after logout (linger): {(IsLingerEnabled() ? "yes" : "no")}");
        Console.WriteLine();

        RunInteractive("systemctl", "--user", "--no-pager", "--lines=15", "status", WatchUnit, TimerUnit, TimerServiceUnit);
        return 0;
    }

    private static void EnableLinger()
    {
        if (IsLingerEnabled())
        {
            Console.WriteLine("It keeps running after you log out (linger is enabled).");
            return;
        }

        var result = Run("loginctl", "enable-linger");
        if (result.ExitCode == 0 && IsLingerEnabled())
        {
            Console.WriteLine("Enabled linger: your sync service starts at boot and keeps running after you log out.");
            return;
        }

        Console.WriteLine($"Could not enable linger ({result.Output.Trim()}), so the sync stops when you log out.");
        Console.WriteLine($"An administrator can enable it with: sudo loginctl enable-linger {Environment.UserName}");
    }

    private static bool IsLingerEnabled()
    {
        return Run("loginctl", "show-user", Environment.UserName, "--property=Linger", "--value").Output.Trim() == "yes"
            || File.Exists(Path.Combine("/var/lib/systemd/linger", Environment.UserName));
    }

    private static void EnsureUserManager()
    {
        (int ExitCode, string Output) result;
        try
        {
            result = Run("systemctl", "--user", "show-environment");
        }
        catch (Win32Exception)
        {
            throw new ServiceException("systemctl was not found; background sync needs systemd.");
        }

        if (result.ExitCode != 0)
        {
            throw new ServiceException($"The systemd user manager is not reachable ({result.Output.Trim()}). Run this from your own login session, not via sudo or su.");
        }
    }

    private static void Systemctl(params string[] args)
    {
        var result = Run("systemctl", ["--user", .. args]);
        if (result.ExitCode != 0)
        {
            throw new ServiceException($"systemctl --user {string.Join(' ', args)} failed: {result.Output.Trim()}");
        }
    }

    private static (int ExitCode, string Output) Run(string fileName, params string[] args)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private static void RunInteractive(string fileName, params string[] args)
    {
        var startInfo = new ProcessStartInfo(fileName) { UseShellExecute = false };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
    }
}
