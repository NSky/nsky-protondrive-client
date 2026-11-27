namespace NSkyLabs.ProtonDrive.Cli.Session;

/// <summary><c>session encrypt|decrypt|status</c>: how the saved login is stored on disk.</summary>
internal static class SessionCommand
{
    public static string Usage => $"""
        Usage:
          {AppInfo.CommandName} session status    Show where the saved login is and whether it is encrypted
          {AppInfo.CommandName} session encrypt   Encrypt it with systemd-creds (systemd 256+), tied to this computer and user
          {AppInfo.CommandName} session decrypt   Store it as a plain file readable only by you again
        """;

    public static int Run(string[] args, SessionStore store)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        switch (args[0])
        {
            case "status":
                Console.WriteLine(Describe(store));
                return 0;

            case "encrypt":
                if (!OperatingSystem.IsLinux())
                {
                    Console.Error.WriteLine("Encrypting the session uses systemd-creds and is only available on Linux.");
                    return 1;
                }

                Console.WriteLine(store.SetEncrypted(true)
                    ? $"The session is now encrypted with systemd-creds: {store.EncryptedFilePath}. Later logins are encrypted too. Each command takes about a second longer to start."
                    : "The session is already encrypted.");
                return 0;

            case "decrypt":
                Console.WriteLine(store.SetEncrypted(false)
                    ? $"The session is now a plain file readable only by you: {store.FilePath}."
                    : "The session is not encrypted.");
                return 0;

            default:
                Console.Error.WriteLine($"Unknown session command '{args[0]}'.");
                Console.Error.WriteLine(Usage);
                return 2;
        }
    }

    /// <summary>One line for <c>session status</c> and <c>whoami</c>.</summary>
    public static string Describe(SessionStore store) => store.IsEncrypted
        ? $"Encrypted with systemd-creds, readable only on this computer by this user: {store.EncryptedFilePath}"
        : $"Plain file, readable only by you (encrypt it with '{AppInfo.CommandName} session encrypt'): {store.FilePath}";
}
