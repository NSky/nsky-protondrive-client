using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace NSkyLabs.ProtonDrive.Cli.Notifications;

/// <summary>
/// Shows a desktop notification through the freedesktop notification service, when the user has a graphical
/// session. Uses <c>notify-send</c> (libnotify) or else <c>gdbus</c> (GLib); on a headless system, or when neither
/// is installed, it does nothing.
/// </summary>
internal static class DesktopNotifier
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static void Show(string title, string body, bool urgent)
    {
        if (!OperatingSystem.IsLinux() || !HasSessionBus())
        {
            return;
        }

        if (TryRun("notify-send", ["--app-name=protondrive", "--icon=" + (urgent ? "dialog-warning" : "dialog-information"), "--urgency=" + (urgent ? "critical" : "normal"), title, body]))
        {
            return;
        }

        TryRun("gdbus",
        [
            "call", "--session",
            "--dest", "org.freedesktop.Notifications",
            "--object-path", "/org/freedesktop/Notifications",
            "--method", "org.freedesktop.Notifications.Notify",
            GVariantString("protondrive"), "0", GVariantString(urgent ? "dialog-warning" : "dialog-information"),
            GVariantString(title), GVariantString(body), "@as []", $"{{'urgency': <byte {(urgent ? 2 : 1)}>}}", "-1",
        ]);
    }

    private static bool HasSessionBus()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
        {
            return true;
        }

        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return !string.IsNullOrEmpty(runtime) && File.Exists(Path.Combine(runtime, "bus"));
    }

    /// <summary>Quotes a string in GVariant text format.</summary>
    private static string GVariantString(string value)
    {
        var text = new StringBuilder("'");
        foreach (var c in value)
        {
            text.Append(c switch
            {
                '\\' => @"\\",
                '\'' => @"\'",
                '\n' => @"\n",
                _ => c.ToString(),
            });
        }

        return text.Append('\'').ToString();
    }

    private static bool TryRun(string fileName, string[] args)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(Timeout))
            {
                process.Kill();
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false; // Not installed.
        }
    }
}
