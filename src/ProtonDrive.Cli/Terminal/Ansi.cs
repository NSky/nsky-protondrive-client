namespace NSkyLabs.ProtonDrive.Cli.Terminal;

/// <summary>
/// Colors for messages on stderr. Used only when stderr is a terminal, and never when the <c>NO_COLOR</c>
/// environment variable is set (https://no-color.org) or <c>TERM=dumb</c>; logs and pipes get plain text.
/// </summary>
internal static class Ansi
{
    public static bool Enabled { get; } =
        !Console.IsErrorRedirected
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
        && Environment.GetEnvironmentVariable("TERM") != "dumb";

    /// <summary>Conflicts and anything that needs the user's attention.</summary>
    public static string Conflict(string text) => Wrap("1;33", text);

    public static string Warning(string text) => Wrap("33", text);

    /// <summary>Deletions and failures.</summary>
    public static string Danger(string text) => Wrap("31", text);

    public static string Error(string text) => Wrap("1;31", text);

    /// <summary>Changes sent to Proton Drive.</summary>
    public static string ToRemote(string text) => Wrap("32", text);

    /// <summary>Changes made locally from Proton Drive.</summary>
    public static string ToLocal(string text) => Wrap("36", text);

    public static string Success(string text) => Wrap("32", text);

    /// <summary>Bookkeeping that changes no files.</summary>
    public static string Dim(string text) => Wrap("2", text);

    private static string Wrap(string code, string text) => Enabled ? $"\u001b[{code}m{text}\u001b[0m" : text;
}
