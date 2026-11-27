using System.Globalization;
using System.Security.Cryptography;
using NSkyLabs.ProtonDrive.Cli.Notifications;
using NSkyLabs.ProtonDrive.Cli.Service;
using NSkyLabs.ProtonDrive.Sync;
using NSkyLabs.ProtonDrive.Sync.State;
using Proton.Drive.Sdk.Nodes;
using Proton.Sdk;
using Proton.Sdk.Api;

namespace NSkyLabs.ProtonDrive.Cli.Sync;

internal sealed class SyncUsageException(string message) : Exception(message);

/// <summary>
/// <c>sync add|remove|list|status</c> manage sync pairs; <c>sync [folder] [--watch]</c> runs the sync.
/// </summary>
internal static class SyncCommand
{
    private const int InvalidRefreshTokenCode = 10013;

    /// <summary>Exit code of <c>sync --watch</c> when the session has ended (see contrib/systemd).</summary>
    private const int SessionEndedExitCode = 3;

    /// <summary>
    /// Exit code of <c>sync --watch</c> under systemd when a new version was installed but the restart could not be
    /// requested: non-zero, so <c>Restart=on-failure</c> starts the new version.
    /// </summary>
    private const int UpdatedExitCode = 75;

    public const string Usage = """
        Usage:
          protondrive sync add <local-folder> <remote-folder>
          protondrive sync remove <local-folder|id>
          protondrive sync list
          protondrive sync status
          protondrive sync [<local-folder|id>] [--watch] [--dry-run] [--force] [--no-progress]
        """;

    public static async Task<int> RunAsync(AuthService auth, string[] args, CancellationToken cancellationToken)
    {
        var flags = args.Where(a => a.StartsWith('-')).ToHashSet(StringComparer.Ordinal);
        var positional = args.Where(a => !a.StartsWith('-')).ToArray();

        var known = new[] { "--watch", "--dry-run", "-n", "--force", "--no-progress", "-q", "--quiet" };
        if (flags.Except(known).FirstOrDefault() is { } unknown)
        {
            throw new SyncUsageException($"Unknown option '{unknown}'.");
        }

        switch (positional.FirstOrDefault())
        {
            case "add":
                Expect(positional, 3);
                return await AddAsync(auth, positional[1], positional[2], cancellationToken).ConfigureAwait(false);

            case "remove":
            case "rm":
                Expect(positional, 2);
                return Remove(positional[1]);

            case "list":
            case "ls":
                Expect(positional, 1);
                return List();

            case "status":
                Expect(positional, 1);
                return Status();

            default:
                if (positional.Length > 1)
                {
                    throw new SyncUsageException("Too many arguments.");
                }

                var showProgress = !flags.Overlaps(["--no-progress", "-q", "--quiet"]);
                var options = new SyncPassOptions { DryRun = flags.Overlaps(["--dry-run", "-n"]), Force = flags.Contains("--force") };

                return flags.Contains("--watch")
                    ? await WatchAsync(auth, positional.FirstOrDefault(), showProgress, cancellationToken).ConfigureAwait(false)
                    : await SyncOnceAsync(auth, positional.FirstOrDefault(), options, showProgress, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Expect(string[] positional, int count)
    {
        if (positional.Length != count)
        {
            throw new SyncUsageException("Wrong number of arguments.");
        }
    }

    private static async Task<int> AddAsync(AuthService auth, string localArgument, string remoteArgument, CancellationToken cancellationToken)
    {
        var localPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localArgument));
        if (Path.GetPathRoot(localPath) == localPath)
        {
            throw new SyncUsageException("Refusing to sync the file system root.");
        }

        if (File.Exists(localPath))
        {
            throw new SyncUsageException($"{localPath} is a file, not a folder.");
        }

        foreach (var appDirectory in new[] { AppInfo.ConfigDirectory, AppInfo.StateDirectory })
        {
            if (IsSameOrInside(localPath, appDirectory) || IsSameOrInside(appDirectory, localPath))
            {
                throw new SyncUsageException($"{localPath} overlaps this app's own folder {appDirectory}.");
            }
        }

        var remotePath = RemotePath.Parse(remoteArgument);
        var remoteDisplay = remotePath.ToString();

        var existing = SyncPairStore.Load();
        foreach (var pair in existing)
        {
            if (IsSameOrInside(localPath, pair.LocalPath) || IsSameOrInside(pair.LocalPath, localPath))
            {
                throw new SyncUsageException($"{localPath} overlaps the synced folder {pair.LocalPath}.");
            }

            if (IsSameOrInsideRemote(remoteDisplay, pair.RemotePath) || IsSameOrInsideRemote(pair.RemotePath, remoteDisplay))
            {
                throw new SyncUsageException($"{remoteDisplay} overlaps the synced remote folder {pair.RemotePath}.");
            }
        }

        var context = await auth.ResumeAsync(cancellationToken).ConfigureAwait(false);
        var browser = DriveBrowser.Create(context);
        var (root, created) = await GetOrCreateRemoteFolderAsync(browser, remotePath, cancellationToken).ConfigureAwait(false);

        var rootUid = root.Uid.ToString();
        if (existing.Any(p => p.RemoteRootUid == rootUid))
        {
            throw new SyncUsageException($"{remoteDisplay} is already synced.");
        }

        var localExisted = Directory.Exists(localPath);
        Directory.CreateDirectory(localPath);
        var localHasContent = Directory.EnumerateFileSystemEntries(localPath).Any();

        var newPair = new SyncPair
        {
            Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)),
            LocalPath = localPath,
            RemotePath = remoteDisplay,
            RemoteRootUid = rootUid,
        };

        SyncPairStore.Update(pairs =>
        {
            pairs.Add(newPair);
            return true;
        });

        Console.WriteLine($"Added {ConsoleSyncObserver.Describe(newPair)} (id {newPair.Id}).");
        if (created)
        {
            Console.WriteLine($"Created {remoteDisplay} on Proton Drive.");
        }

        if (!localExisted)
        {
            Console.WriteLine($"Created {localPath}.");
        }

        if (localHasContent && !created)
        {
            Console.WriteLine("Both folders may already hold files: the first sync merges them. A file that exists on both sides "
                + "with different content is kept twice, the local copy renamed with '(conflict ...)'. "
                + $"Preview with '{AppInfo.CommandName} sync --dry-run {localPath}'.");
        }

        Console.WriteLine($"Run '{AppInfo.CommandName} sync' to sync now, or '{AppInfo.CommandName} sync --watch' to keep it in sync.");
        return 0;
    }

    private static async Task<(FolderNode Folder, bool Created)> GetOrCreateRemoteFolderAsync(
        DriveBrowser browser,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        FolderNode current = await browser.Client.GetMyFilesFolderAsync(cancellationToken).ConfigureAwait(false);
        var created = false;

        foreach (var segment in path.Segments)
        {
            Node? match = null;
            await foreach (var child in browser.ListChildrenAsync(current, cancellationToken).ConfigureAwait(false))
            {
                if (child.Name.TryGetValue(out var name) && name == segment)
                {
                    match = child;
                    break;
                }
            }

            current = match switch
            {
                FolderNode folder => folder,
                null => await browser.Client.CreateFolderAsync(current.Uid, segment, lastModificationTime: null, cancellationToken).ConfigureAwait(false),
                _ => throw new SyncUsageException($"'{segment}' in {path} is a file, not a folder."),
            };

            created |= match is null;
        }

        return (current, created);
    }

    private static int Remove(string idOrPath)
    {
        var removed = SyncPairStore.Update(pairs =>
        {
            var pair = SyncPairStore.Find(pairs, idOrPath);
            if (pair is not null)
            {
                pairs.Remove(pair);
            }

            return pair;
        });

        if (removed is null)
        {
            throw new SyncUsageException($"No sync pair matches '{idOrPath}'. See '{AppInfo.CommandName} sync list'.");
        }

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(removed.DatabasePath + suffix);
        }

        File.Delete(removed.LockPath);
        new SyncAttention().Clear(removed);

        Console.WriteLine($"Removed {ConsoleSyncObserver.Describe(removed)}. No files were changed on either side.");
        return 0;
    }

    private static int List()
    {
        var pairs = SyncPairStore.Load();
        if (pairs.Count == 0)
        {
            Console.WriteLine($"No folders are synced. Add one with '{AppInfo.CommandName} sync add <local-folder> <remote-folder>'.");
            return 0;
        }

        foreach (var pair in pairs)
        {
            Console.WriteLine($"{pair.Id}  {ConsoleSyncObserver.Describe(pair)}");
        }

        return 0;
    }

    private static int Status()
    {
        var pairs = SyncPairStore.Load();
        if (pairs.Count == 0)
        {
            return List();
        }

        foreach (var pair in pairs)
        {
            Console.WriteLine($"{pair.Id}  {ConsoleSyncObserver.Describe(pair)}");

            if (SyncAttention.GetSummary(pair) is { } problem)
            {
                var problemText = problem.Trim();
                var prefix = pair.LocalPath + ": ";
                Console.WriteLine($"    NEEDS ATTENTION: {(problemText.StartsWith(prefix, StringComparison.Ordinal) ? problemText[prefix.Length..] : problemText)}");
            }

            if (!File.Exists(pair.DatabasePath))
            {
                Console.WriteLine("    never synced");
                continue;
            }

            using var state = SyncStateStore.Open(pair.DatabasePath);
            var finished = state.GetMeta("last_pass_finished") is { } text
                ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : "never";

            Console.WriteLine($"    last sync:   {finished} ({state.GetMeta("last_pass_result") ?? "no result"})");
            if (state.GetMeta(SyncEngine.LastCheckedKey) is { } checkedText)
            {
                var lastChecked = DateTimeOffset.Parse(checkedText, CultureInfo.InvariantCulture).ToLocalTime();
                Console.WriteLine($"    last checked: {lastChecked.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
            }
            Console.WriteLine($"    synced items: {state.CountBase()}");

            if (state.GetMeta(SyncEngine.HeldDeletionsKey) is { } heldText && int.TryParse(heldText, CultureInfo.InvariantCulture, out var held) && held > 0)
            {
                Console.WriteLine(state.GetMeta(SyncEngine.HeldSideKey) == "local"
                    ? $"    The remote folder is empty: {held} local item(s) are kept and syncing is paused until you restore them on Proton Drive or confirm their deletion."
                    : $"    {held} item(s) deleted locally are waiting for confirmation before being deleted on Proton Drive.");
                Console.WriteLine($"    Review with '{AppInfo.CommandName} sync --dry-run {pair.LocalPath}', confirm with '{AppInfo.CommandName} sync --force {pair.LocalPath}'.");
            }

            using var probe = Core.IO.FileLock.TryAcquire(pair.LockPath);
            if (probe is null)
            {
                Console.WriteLine("    a sync is running now");
            }
        }

        return 0;
    }

    private static IReadOnlyList<SyncPair> SelectPairs(string? idOrPath)
    {
        var pairs = SyncPairStore.Load();
        if (pairs.Count == 0)
        {
            throw new SyncUsageException($"No folders are synced. Add one with '{AppInfo.CommandName} sync add <local-folder> <remote-folder>'.");
        }

        if (idOrPath is null)
        {
            return pairs;
        }

        return SyncPairStore.Find(pairs, idOrPath) is { } pair
            ? [pair]
            : throw new SyncUsageException($"No sync pair matches '{idOrPath}'. See '{AppInfo.CommandName} sync list'.");
    }

    private static async Task<int> SyncOnceAsync(AuthService auth, string? idOrPath, SyncPassOptions options, bool showProgress, CancellationToken cancellationToken)
    {
        var pairs = SelectPairs(idOrPath);
        var reporter = new AttentionReporter(pairs);
        var context = await ResumeAsync(auth, pairs, reporter, cancellationToken).ConfigureAwait(false);
        var observer = new ConsoleSyncObserver(showProgress, quietWhenUpToDate: false, onPassFinished: reporter.UpdateStatus);
        var engine = new SyncEngine(DriveBrowser.Create(context).Client, observer, reporter.Attention);

        var exitCode = 0;
        foreach (var pair in pairs)
        {
            try
            {
                var result = await engine.RunAsync(pair, options, cancellationToken).ConfigureAwait(false);
                // Held deletions need the user's attention, so cron and the timer unit report them as a failure.
                if (result.Outcome == SyncPassOutcome.Refused || result.Failed > 0 || result.HeldDeletions > 0)
                {
                    exitCode = 1;
                }
            }
            catch (Exception ex) when (ex is SyncRootUnavailableException or IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"{ConsoleSyncObserver.Describe(pair)}: {ex.Message}");
                exitCode = 1;
            }
        }

        return exitCode;
    }

    private static async Task<int> WatchAsync(AuthService auth, string? idOrPath, bool showProgress, CancellationToken cancellationToken)
    {
        var pairs = SelectPairs(idOrPath);
        var reporter = new AttentionReporter(pairs);

        AuthenticatedContext context;
        try
        {
            context = await ResumeAsync(auth, pairs, reporter, cancellationToken).ConfigureAwait(false);
        }
        catch (NotLoggedInException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return SessionEndedExitCode;
        }

        var observer = new ConsoleSyncObserver(showProgress, quietWhenUpToDate: true, onPassFinished: reporter.UpdateStatus);
        var engine = new SyncEngine(DriveBrowser.Create(context).Client, observer, reporter.Attention);
        reporter.UpdateStatus();

        Console.Error.WriteLine($"{AppInfo.CommandName} {AppInfo.Version}");
        Console.Error.WriteLine($"Watching {pairs.Count} folder(s). Press Ctrl+C to stop.");
        foreach (var pair in pairs)
        {
            Console.Error.WriteLine($"  {ConsoleSyncObserver.Describe(pair)}");
        }

        // A new version installed while watching: under systemd, restart on it once no pass is running; in a
        // terminal, say so once and keep going.
        var updates = UpdateWatcher.Create();
        var underSystemd = Environment.GetEnvironmentVariable("INVOCATION_ID") is { Length: > 0 };
        var updateNoted = false;
        bool StopForUpdate()
        {
            if (updates is null || updateNoted || !updates.IsUpdateReady())
            {
                return false;
            }

            if (underSystemd)
            {
                return true;
            }

            updateNoted = true;
            Console.Error.WriteLine($"A new version was installed ({updates.NewVersion}). Restart '{AppInfo.CommandName} sync --watch' to use it.");
            return false;
        }

        var service = new SyncWatchService(engine, pairs, new SyncWatchOptions { StopWhenIdle = StopForUpdate }, observer);
        try
        {
            await service.RunAsync(IsFatal, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFatal(ex))
        {
            foreach (var pair in pairs)
            {
                reporter.Attention.Raise(pair, SyncAttention.SessionEndedNotice);
            }

            // A distinct exit code lets the systemd unit stop restarting until the user logs in again.
            Console.Error.WriteLine($"The session has ended ({ex.Message}). Run '{AppInfo.CommandName} login', then restart the sync.");
            return SessionEndedExitCode;
        }

        if (updates?.NewVersion is { } newVersion && underSystemd && !cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine($"A new version was installed ({newVersion}); restarting on it now that no sync is running.");
            SystemdStatus.Set($"Restarting on the new version ({newVersion})");
            if (!UpdateWatcher.RequestServiceRestart())
            {
                return UpdatedExitCode;
            }

            // systemd stops this process as part of the restart; wait for it rather than racing it.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }

            return UpdatedExitCode;
        }

        Console.Error.WriteLine("Stopped.");
        return 0;
    }

    /// <summary>Resumes the session; if there is none, marks every folder as needing a new login.</summary>
    private static async Task<AuthenticatedContext> ResumeAsync(
        AuthService auth,
        IReadOnlyList<SyncPair> pairs,
        AttentionReporter reporter,
        CancellationToken cancellationToken)
    {
        try
        {
            return await auth.ResumeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (NotLoggedInException)
        {
            foreach (var pair in pairs)
            {
                reporter.Attention.Raise(pair, SyncAttention.SessionEndedNotice);
            }

            throw;
        }
    }

    /// <summary>Errors that retrying will not fix: the session is gone, so the user must log in again.</summary>
    private static bool IsFatal(Exception ex) => ex is NotLoggedInException or ProtonApiException { Code: InvalidRefreshTokenCode };

    private static bool IsSameOrInside(string path, string folder)
    {
        var relative = Path.GetRelativePath(folder, path);
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }

    private static bool IsSameOrInsideRemote(string path, string folder)
    {
        return folder == "/" || path == folder || path.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal);
    }
}
