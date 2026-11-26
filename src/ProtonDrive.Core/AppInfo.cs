using System.Reflection;

namespace NSkyLabs.ProtonDrive.Core;

internal static class AppInfo
{
    public const string CommandName = "protondrive";

    /// <summary>One-line form of <see cref="Disclaimer"/>.</summary>
    public const string ShortNotice = "BETA software, provided as is. Unofficial; not affiliated with Proton AG.";

    /// <summary>Shown in the help text, at login and when a folder is added for sync.</summary>
    public const string Disclaimer = """
        BETA VERSION. This is unofficial, third-party software. It is not affiliated with, endorsed by or
        supported by Proton AG. It is provided "as is", without warranty of any kind, and the authors accept no
        responsibility or liability for any loss of data, damage to your account or other harm resulting from its
        use. Keep independent backups of anything important.
        """;

    /// <summary>Semantic version of this build (major.minor.patch).</summary>
    public static string Version { get; } = GetVersion();

    /// <summary>
    /// Value of the <c>x-pm-appversion</c> header. Proton requires third-party clients to identify themselves
    /// honestly using the shape <c>external-drive-{name}@{semver}-{channel}</c>.
    /// </summary>
    public static string ProtonAppVersion { get; } = $"external-drive-nsky_protondrive@{Version}-beta";

    public static string UserAgent { get; } = $"{CommandName}/{Version} ({Environment.OSVersion.Platform})";

    /// <summary>Per-user configuration directory, e.g. <c>~/.config/protondrive</c> on Linux.</summary>
    public static string ConfigDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "protondrive");

    /// <summary>
    /// Per-user state directory (sync databases, locks): <c>$XDG_STATE_HOME/protondrive</c>, by default
    /// <c>~/.local/state/protondrive</c> on Linux; the local application data folder elsewhere.
    /// </summary>
    public static string StateDirectory { get; } = GetStateDirectory();

    private static string GetVersion()
    {
        var version = typeof(AppInfo).Assembly.GetName().Version ?? new Version(0, 0, 0);
        return $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }

    private static string GetStateDirectory()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            var xdgState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            var root = !string.IsNullOrEmpty(xdgState) && Path.IsPathRooted(xdgState)
                ? xdgState
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");

            return Path.Combine(root, "protondrive");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "protondrive",
            "state");
    }
}

/// <summary>Creates directories that only the current user can access.</summary>
internal static class PrivateDirectory
{
    private const UnixFileMode Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static string Ensure(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, Mode);
        }

        return path;
    }
}
