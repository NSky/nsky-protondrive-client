using System.Runtime.InteropServices;
using NSkyLabs.ProtonDrive.Cli.Drive;
using NSkyLabs.ProtonDrive.Cli.Service;
using NSkyLabs.ProtonDrive.Cli.Session;
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

// Names from Proton Drive and the disk must not control the terminal (escape sequences in file names).
TerminalOutput.Install();

// What a long step is doing: a live status line on a terminal, an occasional line in logs.
await using var activityStatus = ActivityStatus.Start(showLiveLine: !args.Any(a => a is "--no-progress" or "-q" or "--quiet"));

var sessionStore = new SessionStore(AppInfo.ConfigDirectory);
var auth = new AuthService(sessionStore);
var command = args.Length > 0 ? args[0] : "help";

// The progress flags only turn off the status line here; commands without a transfer display ignore them.
var commandArgs = args.Where(a => a is not ("--no-progress" or "-q" or "--quiet")).ToArray();

try
{
    switch (command)
    {
        case "login":
        {
            var username = GetOption(args, "--username", "-u");
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
            Console.WriteLine($"Saved login:   {SessionCommand.Describe(sessionStore)}");
            return 0;
        }

        case "ls":
        case "list":
        {
            var context = await auth.ResumeAsync(cts.Token);
            return await ListCommand.RunAsync(context, commandArgs.Length > 1 ? commandArgs[1] : null, cts.Token);
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
            var flags = commandArgs.Skip(1).Where(a => a.StartsWith('-')).ToHashSet(StringComparer.Ordinal);
            var positional = commandArgs.Skip(1).Where(a => !a.StartsWith('-')).ToArray();
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
            var positional = commandArgs.Skip(1).Where(a => !a.StartsWith('-')).ToArray();
            if (positional.Length < 2 || commandArgs.Skip(1).Any(a => a.StartsWith('-')))
            {
                Console.Error.WriteLine(MoveCommand.Usage);
                return 2;
            }

            var context = await auth.ResumeAsync(cts.Token);
            return await MoveCommand.RunAsync(context, positional, cts.Token);
        }

        case "mkdir":
        {
            var flags = commandArgs.Skip(1).Where(a => a.StartsWith('-')).ToHashSet(StringComparer.Ordinal);
            var positional = commandArgs.Skip(1).Where(a => !a.StartsWith('-')).ToArray();

            if (positional.Length == 0 || flags.Except(["--parents", "-p"]).Any())
            {
                Console.Error.WriteLine(MkdirCommand.Usage);
                return 2;
            }

            var context = await auth.ResumeAsync(cts.Token);
            return await MkdirCommand.RunAsync(context, positional, flags.Overlaps(["--parents", "-p"]), cts.Token);
        }

        case "sync":
            return await SyncCommand.RunAsync(auth, args[1..], cts.Token);

        case "service":
            return ServiceCommand.Run(args[1..], sessionStore);

        case "session":
            return SessionCommand.Run(commandArgs[1..], sessionStore);

        case "version":
        case "--version":
            Console.WriteLine($"{AppInfo.CommandName} {AppInfo.Version} ({AppInfo.ProtonAppVersion})");
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
    // Bold headings on a terminal; plain text when piped or with NO_COLOR.
    var bold = !Console.IsOutputRedirected
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
        && Environment.GetEnvironmentVariable("TERM") != "dumb";
    string H(string text) => bold ? $"\u001b[1m{text}\u001b[0m" : text;

    var name = AppInfo.CommandName;
    var notice = string.Join(Environment.NewLine, AppInfo.Disclaimer.Split('\n').Select(line => "  " + line.TrimEnd('\r')));
    Console.WriteLine($"""
        {H($"{name} {AppInfo.Version}")}: an unofficial command-line client for Proton Drive

        {H("Usage:")} {name} <command> [arguments] [options]
        Remote paths are in My files and start with /, for example /Documents/report.pdf.

        {H("Account")}
          login                       Sign in; the password, 2FA code and mailbox password are asked for
              -u, --username <name>     the account to sign in with
          whoami                      Show the signed-in account and check the session (also: status)
          logout                      Sign out and remove the saved credentials
          session status              Show where the saved login is kept and whether it is encrypted
          session encrypt             Encrypt the saved login with systemd-creds, tied to this computer
          session decrypt             Keep the saved login as a plain file readable only by you

        {H("Files on Proton Drive")}
          ls [path]                   List a folder, one level (default: the root; also: list)
          download <path> <folder>    Download a file, or a folder's contents, into a local folder;
                                      the folder must be empty or not exist yet (also: get)
          upload <local-path> [path]  Upload a file or folder into an existing folder (default: the root);
                                      if any name already exists there, nothing is uploaded (also: put)
          mkdir <path>...             Create folders
              -p, --parents             also create missing parent folders; existing folders are fine
          move <source>... <dest>     Move items into a folder, or move or rename one item; nothing is
                                      ever overwritten (also: mv)
          delete <path>...            Move files or folders to the Proton Drive trash; asks first (also: rm)
              -r, --recursive           needed to delete a folder
              --permanent               delete permanently instead of moving to the trash
              -y, --yes                 do not ask first

        {H("Sync")}
          sync add <folder> <path>    Start syncing a local folder with a folder on Proton Drive
          sync remove <folder|id>     Stop syncing a folder; no files are changed
          sync list                   Show synced folders and their last sync (also: sync status)
          sync [<folder|id>]          Sync all folders, or one, once
              --watch                   keep syncing until stopped
              -n, --dry-run             only show what would be done
              --force                   skip the safety checks for large deletions

        {H("Background sync")}
          service enable              Keep syncing as a systemd user service, also after logging out
              --timer                   sync every 15 minutes instead of continuously
          service disable             Stop background sync
          service status              Show the state of background sync

        {H("Other")}
          version                     Show version information
          help                        Show this help

        {H("Options for all commands")}
          -q, --no-progress           No status line or progress display

        {H("Files")}
          Settings and session: {AppInfo.ConfigDirectory}
          Sync state:           {AppInfo.StateDirectory}

        {H("Notice")}
        {notice}
        """);
}
