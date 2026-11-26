using System.Runtime.InteropServices;
using NSkyLabs.ProtonDrive.Cli.Drive;
using NSkyLabs.ProtonDrive.Cli.Service;
using NSkyLabs.ProtonDrive.Cli.Sync;
using NSkyLabs.ProtonDrive.Cli.Terminal;
using NSkyLabs.ProtonDrive.Sync;
using Proton.Sdk.Api;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// systemd stops services with SIGTERM: finish the current step and exit cleanly.
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    cts.Cancel();
});

// What a long step is doing: a live status line on a terminal, an occasional line in logs.
await using var activityStatus = ActivityStatus.Start(showLiveLine: !args.Any(a => a is "--no-progress" or "-q" or "--quiet"));

var sessionStore = new SessionStore(AppInfo.ConfigDirectory);
var auth = new AuthService(sessionStore);
var command = args.Length > 0 ? args[0] : "help";

try
{
    switch (command)
    {
        case "login":
        {
            var username = GetOption(args, "--username", "-u");
            Console.Error.WriteLine(AppInfo.Disclaimer);
            Console.Error.WriteLine();
            var context = await auth.LoginAsync(new ConsoleLoginPrompter(), username, cts.Token);
            var address = await context.AccountClient.GetCurrentUserDefaultAddressAsync(cts.Token);
            Console.WriteLine($"Logged in as {context.Session.Username} <{address.EmailAddress}>.");
            return 0;
        }

        case "logout":
        {
            var (wasLoggedIn, warning) = await auth.LogoutAsync(cts.Token);
            if (warning is not null)
            {
                Console.Error.WriteLine($"Warning: {warning}");
            }

            Console.WriteLine(wasLoggedIn ? "Logged out." : "Not logged in.");
            return 0;
        }

        case "whoami":
        case "status":
        {
            var context = await auth.ResumeAsync(cts.Token);
            var address = await context.AccountClient.GetCurrentUserDefaultAddressAsync(cts.Token);
            var keyState = "locked";
            try
            {
                await context.AccountClient.GetAddressPrimaryPrivateKeyAsync(address.Id, cts.Token);
                keyState = "unlocked";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                keyState = $"locked ({ex.Message})";
            }

            Console.WriteLine($"User:          {context.Session.Username}");
            Console.WriteLine($"Email:         {address.EmailAddress}");
            Console.WriteLine($"User ID:       {context.Session.UserId}");
            Console.WriteLine($"Session ID:    {context.Session.SessionId}");
            Console.WriteLine($"Password mode: {context.Session.PasswordMode}");
            Console.WriteLine($"Account keys:  {keyState}");
            Console.WriteLine($"Logged in at:  {context.Stored.CreatedAt.ToLocalTime():u}");
            return 0;
        }

        case "ls":
        case "list":
        {
            var context = await auth.ResumeAsync(cts.Token);
            return await ListCommand.RunAsync(context, args.Length > 1 ? args[1] : null, cts.Token);
        }

        case "download":
        case "get":
        {
            var flags = args.Skip(1).Where(a => a.StartsWith('-')).ToHashSet(StringComparer.Ordinal);
            var positional = args.Skip(1).Where(a => !a.StartsWith('-')).ToArray();
            var unknownFlags = flags.Except(["--no-progress", "-q", "--quiet"]).ToArray();

            if (positional.Length != 2 || unknownFlags.Length > 0)
            {
                Console.Error.WriteLine($"Usage: {AppInfo.CommandName} download <remote-path> <local-folder> [--no-progress]");
                return 2;
            }

            var showProgress = !flags.Overlaps(["--no-progress", "-q", "--quiet"]);
            var context = await auth.ResumeAsync(cts.Token);
            return await DownloadCommand.RunAsync(context, positional[0], positional[1], showProgress, cts.Token);
        }

        case "upload":
        case "put":
        {
            var flags = args.Skip(1).Where(a => a.StartsWith('-')).ToHashSet(StringComparer.Ordinal);
            var positional = args.Skip(1).Where(a => !a.StartsWith('-')).ToArray();
            var unknownFlags = flags.Except(["--no-progress", "-q", "--quiet"]).ToArray();

            if (positional.Length is < 1 or > 2 || unknownFlags.Length > 0)
            {
                Console.Error.WriteLine($"Usage: {AppInfo.CommandName} upload <local-path> [remote-folder] [--no-progress]");
                return 2;
            }

            var showProgress = !flags.Overlaps(["--no-progress", "-q", "--quiet"]);
            var context = await auth.ResumeAsync(cts.Token);
            return await UploadCommand.RunAsync(context, positional[0], positional.ElementAtOrDefault(1), showProgress, cts.Token);
        }

        case "delete":
        case "rm":
        {
            var flags = args.Skip(1).Where(a => a.StartsWith('-')).ToHashSet(StringComparer.Ordinal);
            var positional = args.Skip(1).Where(a => !a.StartsWith('-')).ToArray();
            var unknownFlags = flags.Except(["--recursive", "-r", "--permanent", "--yes", "-y"]).ToArray();

            if (positional.Length == 0 || unknownFlags.Length > 0)
            {
                Console.Error.WriteLine(DeleteCommand.Usage);
                return 2;
            }

            var context = await auth.ResumeAsync(cts.Token);
            return await DeleteCommand.RunAsync(context, positional, flags.Overlaps(["--recursive", "-r"]), flags.Contains("--permanent"), flags.Overlaps(["--yes", "-y"]), cts.Token);
        }

        case "move":
        case "mv":
        {
            var positional = args.Skip(1).Where(a => !a.StartsWith('-')).ToArray();
            if (positional.Length < 2 || args.Skip(1).Any(a => a.StartsWith('-')))
            {
                Console.Error.WriteLine(MoveCommand.Usage);
                return 2;
            }

            var context = await auth.ResumeAsync(cts.Token);
            return await MoveCommand.RunAsync(context, positional, cts.Token);
        }

        case "sync":
            return await SyncCommand.RunAsync(auth, args[1..], cts.Token);

        case "service":
            return ServiceCommand.Run(args[1..], sessionStore);

        case "version":
        case "--version":
            Console.WriteLine($"{AppInfo.CommandName} {AppInfo.Version} ({AppInfo.ProtonAppVersion})");
            Console.WriteLine(AppInfo.ShortNotice);
            return 0;

        case "help":
        case "--help":
        case "-h":
            PrintHelp();
            return 0;

        default:
            Console.Error.WriteLine($"Unknown command '{command}'.");
            PrintHelp();
            return 2;
    }
}
catch (NotLoggedInException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
catch (ServiceException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(ServiceCommand.Usage);
    return 1;
}
catch (SyncUsageException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(SyncCommand.Usage);
    return 2;
}
catch (Exception ex) when (ex is RemotePathNotFoundException or DownloadTargetException or UploadTargetException or SyncRootUnavailableException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
catch (ProtonApiException ex)
{
    Console.Error.WriteLine(DescribeApiError(ex));
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    if (ex.InnerException is not null)
    {
        Console.Error.WriteLine($"  Caused by: {ex.InnerException.Message}");
    }

    return 1;
}

static string? GetOption(string[] args, string longName, string shortName)
{
    for (var i = 1; i < args.Length - 1; i++)
    {
        if (args[i] == longName || args[i] == shortName)
        {
            return args[i + 1];
        }
    }

    return null;
}

static string DescribeApiError(ProtonApiException ex)
{
    var hint = ex.Code switch
    {
        9001 => " Human verification (CAPTCHA) was requested; this client cannot complete it yet. Try signing in via the web once, then retry later.",
        10013 => $" The session has expired. Run '{AppInfo.CommandName} login' again.",
        _ => string.Empty,
    };

    return $"Proton API error {ex.Code}: {ex.Message?.TrimEnd('.')}.{hint}";
}

static void PrintHelp()
{
    Console.WriteLine($"""
        {AppInfo.CommandName} {AppInfo.Version} (beta) - unofficial Proton Drive command-line client

        {AppInfo.Disclaimer}

        Usage: {AppInfo.CommandName} <command> [options]

        Commands:
          login [-u|--username <name>]  Sign in (password, 2FA and mailbox password are prompted)
          whoami | status               Show the signed-in account and verify the session
          logout                        End the session on the server and remove local credentials
          ls | list [path]              List a folder in My files (one level; default: the root)
          download | get <remote-path> <local-folder> [--no-progress]
                                        Download a file into <local-folder>, or a folder's contents
                                        into <local-folder> (which must be empty or not exist)
          upload | put <local-path> [remote-folder] [--no-progress]
                                        Upload a file or a folder into an existing remote folder
                                        (default: the root). Nothing on the server is overwritten:
                                        if any name already exists, nothing is uploaded
          move | mv <source>... <destination>
                                        Move files or folders into an existing folder, or move/rename one
                                        item to a new path. Nothing is overwritten
          delete | rm <remote-path>... [-r|--recursive] [--permanent] [-y|--yes]
                                        Move files or folders to the Proton Drive trash (or delete them
                                        permanently with --permanent). Folders need --recursive.
                                        Asks first unless --yes is given
          sync add <local-folder> <remote-folder>
                                        Keep a local folder and a remote folder in sync
          sync remove <local-folder|id> Stop syncing a folder (no files are changed)
          sync list | sync status       Show synced folders and their last sync
          sync [<local-folder|id>] [--watch] [--dry-run] [--force] [--no-progress]
                                        Sync all folders (or one) once, or keep syncing with --watch.
                                        --dry-run shows the plan; --force skips the safety checks
          service enable [--timer] | disable | status
                                        Sync in the background as a systemd user service, also after
                                        logging out (continuously, or every 15 minutes with --timer)
          version                       Print version information
          help                          Show this help

        Session and sync settings are stored in {AppInfo.ConfigDirectory}, sync state in {AppInfo.StateDirectory}
        """);
}
