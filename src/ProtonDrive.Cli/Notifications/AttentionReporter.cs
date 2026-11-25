using System.Globalization;
using NSkyLabs.ProtonDrive.Sync;

namespace NSkyLabs.ProtonDrive.Cli.Notifications;

/// <summary>
/// Reports attention changes beyond the notice file and the login summary: a log line, a desktop notification
/// (when running in the background), and the systemd status line.
/// </summary>
internal sealed class AttentionReporter
{
    private readonly IReadOnlyList<SyncPair> _pairs;
    private readonly bool _background = Console.IsErrorRedirected;

    public AttentionReporter(IReadOnlyList<SyncPair> pairs)
    {
        _pairs = pairs;
        Attention = new SyncAttention(OnChanged);
    }

    public SyncAttention Attention { get; }

    /// <summary>Refreshes the systemd status line from the folders' current state.</summary>
    public void UpdateStatus()
    {
        if (!SystemdStatus.IsAvailable)
        {
            return;
        }

        var problems = _pairs.Select(p => (Pair: p, Summary: SyncAttention.GetSummary(p))).Where(x => x.Summary is not null).ToList();
        var time = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);

        SystemdStatus.Set(problems.Count switch
        {
            0 => $"{_pairs.Count} folder(s) in sync, last checked {time}",
            1 => $"Needs attention: {problems[0].Summary!.Trim()}",
            _ => $"{problems.Count} of {_pairs.Count} folders need attention; run 'protondrive sync status'",
        });
    }

    private void OnChanged(SyncPair pair, AttentionNotice? notice)
    {
        if (notice is null)
        {
            Console.Error.WriteLine($"{pair.LocalPath}: the problem is resolved; syncing normally again.");
            if (_background)
            {
                DesktopNotifier.Show("Proton Drive sync resumed", $"{pair.LocalPath} is syncing normally again.", urgent: false);
            }
        }
        else
        {
            Console.Error.WriteLine($"Attention: {pair.LocalPath}: {notice.Summary}. Details: {SyncAttention.NoticePath(pair)}");
            if (_background)
            {
                var where = Directory.Exists(pair.LocalPath) ? $"\nDetails: {SyncAttention.NoticePath(pair)}" : string.Empty;
                DesktopNotifier.Show("Proton Drive sync needs attention", $"{pair.LocalPath}: {notice.Summary}.{where}", urgent: true);
            }
        }

        UpdateStatus();
    }
}
