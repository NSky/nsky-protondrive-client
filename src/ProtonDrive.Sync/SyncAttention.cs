using System.Globalization;
using System.Text;

namespace NSkyLabs.ProtonDrive.Sync;

internal enum AttentionKind
{
    /// <summary>Many local deletions wait for confirmation before being applied on Proton Drive.</summary>
    DeletionsHeld,

    /// <summary>The remote folder is suddenly empty: local files are kept and the folder is paused.</summary>
    RemoteFolderEmpty,

    /// <summary>The local folder is suddenly empty: nothing is synced.</summary>
    LocalFolderEmpty,

    /// <summary>The local or the remote folder cannot be used (missing, unreadable, trashed).</summary>
    FolderUnavailable,

    /// <summary>Sync has failed several times in a row.</summary>
    Failing,

    /// <summary>The Proton session has ended; the user must log in again.</summary>
    SessionEnded,
}

/// <summary>A problem with a synced folder that the user has to act on.</summary>
/// <param name="Summary">One line, shown at login, in notifications and in the systemd status.</param>
/// <param name="Details">The full explanation and what to do, written to the notice file.</param>
internal sealed record AttentionNotice(AttentionKind Kind, string Summary, string Details);

/// <summary>
/// Makes problems visible without reading logs. For each synced folder that needs attention:
/// <list type="bullet">
/// <item>a notice file in the folder itself (<see cref="NoticeFileName"/>), never synced;</item>
/// <item>a one-line summary in <c>~/.local/state/protondrive/attention/</c>, printed at login by
/// <c>/etc/profile.d/protondrive.sh</c> and shown by <c>sync status</c>;</item>
/// <item>a callback to the front end, which shows a desktop notification and the systemd status line.</item>
/// </list>
/// Everything is removed as soon as the problem is resolved.
/// </summary>
internal sealed class SyncAttention(Action<SyncPair, AttentionNotice?>? changed = null)
{
    /// <summary>The notice file at the root of a synced folder. Upper case so it stands out; never synced.</summary>
    public const string NoticeFileName = "PROTONDRIVE-SYNC-NEEDS-ATTENTION.txt";

    public static string SummaryDirectory => Path.Combine(AppInfo.StateDirectory, "attention");

    public static AttentionNotice SessionEndedNotice { get; } = new(
        AttentionKind.SessionEnded,
        "sync stopped: the Proton session has ended, log in again",
        $"""
        The Proton session has ended (it was signed out, or it expired), so nothing is synced.

        Log in again and restart background sync:
          {AppInfo.CommandName} login
          {AppInfo.CommandName} service enable
        """);

    public static string SummaryPath(SyncPair pair) => Path.Combine(SummaryDirectory, pair.Id);

    public static string NoticePath(SyncPair pair) => Path.Combine(pair.LocalPath, NoticeFileName);

    /// <summary>The current summary line of a folder, or <see langword="null"/> if it is fine.</summary>
    public static string? GetSummary(SyncPair pair)
    {
        try
        {
            return File.Exists(SummaryPath(pair)) ? File.ReadAllText(SummaryPath(pair)).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Raise(SyncPair pair, AttentionNotice notice)
    {
        var summaryLine = FormatSummary(pair, notice);
        var previous = GetSummary(pair);

        PrivateDirectory.Ensure(SummaryDirectory);
        WriteIfChanged(SummaryPath(pair), summaryLine + "\n");

        // The folder may be missing (an unmounted disk); never create it just to put the notice there.
        if (Directory.Exists(pair.LocalPath))
        {
            TryWriteIfChanged(NoticePath(pair), FormatNotice(pair, notice));
        }

        if (previous != summaryLine)
        {
            changed?.Invoke(pair, notice);
        }
    }

    public void Clear(SyncPair pair)
    {
        var hadNotice = File.Exists(SummaryPath(pair));
        TryDelete(SummaryPath(pair));
        TryDelete(NoticePath(pair));

        if (hadNotice)
        {
            changed?.Invoke(pair, null);
        }
    }

    private static string FormatSummary(SyncPair pair, AttentionNotice notice)
    {
        var where = Directory.Exists(pair.LocalPath) ? $" See {NoticePath(pair)}" : string.Empty;
        return $"  {pair.LocalPath}: {notice.Summary}.{where}";
    }

    private static string FormatNotice(SyncPair pair, AttentionNotice notice)
    {
        var text = new StringBuilder();
        text.AppendLine("PROTONDRIVE SYNC NEEDS YOUR ATTENTION");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Folder:  {pair.LocalPath}  <->  Proton Drive {pair.RemotePath}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Problem: {notice.Summary}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Updated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");
        text.AppendLine();
        text.AppendLine(notice.Details.TrimEnd());
        text.AppendLine();
        text.AppendLine($"Current state: {AppInfo.CommandName} sync status");
        text.AppendLine();
        text.AppendLine("This file was created by protondrive. It is not synced, and it is removed automatically once the");
        text.AppendLine("problem is resolved.");
        return text.ToString();
    }

    /// <summary>Writes only when the content changes (ignoring the "Updated" line), so file events do not loop.</summary>
    private static void WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && WithoutTimestamp(File.ReadAllText(path)) == WithoutTimestamp(content))
        {
            return;
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static void TryWriteIfChanged(string path, string content)
    {
        try
        {
            WriteIfChanged(path, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the summary and the notification still report the problem.
        }
    }

    private static string WithoutTimestamp(string text) =>
        string.Join('\n', text.Split('\n').Where(line => !line.StartsWith("Updated: ", StringComparison.Ordinal)));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
