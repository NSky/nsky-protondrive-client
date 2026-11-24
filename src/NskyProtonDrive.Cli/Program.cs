using NskyProtonDrive.Cli;
using NskyProtonDrive.Cli.Auth;
using NskyProtonDrive.Cli.Drive;
using Proton.Sdk.Api;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var auth = new AuthService(new SessionStore(AppInfo.ConfigDirectory));
var command = args.Length > 0 ? args[0] : "help";

try
{
    switch (command)
    {
        case "login":
        {
            var username = GetOption(args, "--username", "-u");
            var context = await auth.LoginAsync(username, cts.Token);
            var address = await context.AccountClient.GetCurrentUserDefaultAddressAsync(cts.Token);
            Console.WriteLine($"Logged in as {context.Session.Username} <{address.EmailAddress}>.");
            return 0;
        }

        case "logout":
            Console.WriteLine(await auth.LogoutAsync(cts.Token) ? "Logged out." : "Not logged in.");
            return 0;

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
catch (Exception ex) when (ex is RemotePathNotFoundException or DownloadTargetException or UploadTargetException)
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
        {AppInfo.CommandName} {AppInfo.Version} - unofficial Proton Drive command-line client
        {AppInfo.ThirdPartyNotice}

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
          version                       Print version information
          help                          Show this help

        Session data is stored in {AppInfo.ConfigDirectory}
        """);
}
