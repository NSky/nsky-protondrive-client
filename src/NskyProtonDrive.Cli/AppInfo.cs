using System.Reflection;

namespace NskyProtonDrive.Cli;

internal static class AppInfo
{
    public const string CommandName = "protondrive";

    public const string ThirdPartyNotice = "This is a third-party application not officially supported by Proton.";

    /// <summary>Semantic version of this build (major.minor.patch).</summary>
    public static string Version { get; } = GetVersion();

    /// <summary>
    /// Value of the <c>x-pm-appversion</c> header. Proton requires third-party clients to identify themselves
    /// honestly using the shape <c>external-drive-{name}@{semver}-{channel}</c>.
    /// </summary>
    public static string ProtonAppVersion { get; } = $"external-drive-nsky_protondrive@{Version}-alpha";

    public static string UserAgent { get; } = $"{CommandName}/{Version} ({Environment.OSVersion.Platform})";

    /// <summary>Per-user configuration directory, e.g. <c>~/.config/protondrive</c> on Linux.</summary>
    public static string ConfigDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "protondrive");

    private static string GetVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        return $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }
}
