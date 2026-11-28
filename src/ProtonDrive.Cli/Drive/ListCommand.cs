using NSkyLabs.ProtonDrive.Cli.Terminal;
using NSkyLabs.ProtonDrive.Core.Diagnostics;
using System.Globalization;
using NSkyLabs.ProtonDrive.Core.Auth;
using Proton.Drive.Sdk.Nodes;

namespace NSkyLabs.ProtonDrive.Cli.Drive;

/// <summary>
/// <c>ls [path]</c>: lists one level of a folder in "My files" (or a single file).
/// </summary>
internal static class ListCommand
{
    public static async Task<int> RunAsync(AuthenticatedContext context, string? pathArgument, CancellationToken cancellationToken)
    {
        var path = RemotePath.Parse(pathArgument);
        var browser = DriveBrowser.Create(context);

        var node = await browser.ResolveAsync(path, cancellationToken).ConfigureAwait(false);

        List<Node> entries;
        if (node is FolderNode folder)
        {
            entries = [];
            using var activity = Activity.Begin($"Listing {path}", "items");
            await foreach (var child in browser.ListChildrenAsync(folder, cancellationToken).ConfigureAwait(false))
            {
                entries.Add(child);
                activity.Increment();
            }
        }
        else
        {
            entries = [node];
        }

        var rows = entries
            .Select(ToRow)
            .OrderBy(r => r.IsFolder ? 0 : 1)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        if (rows.Count == 0)
        {
            Console.Error.WriteLine($"{path}: empty folder");
            return 0;
        }

        var sizeWidth = Math.Max(4, rows.Max(r => r.Size.Length));

        foreach (var row in rows)
        {
            Console.WriteLine($"{row.Type}  {row.Modified}  {row.Size.PadLeft(sizeWidth)}  {TerminalOutput.OutputName(row.Name)}{(row.IsFolder ? "/" : string.Empty)}");
        }

        return 0;
    }

    private static Row ToRow(Node node)
    {
        var name = DriveBrowser.GetName(node);

        return node switch
        {
            FolderNode folder => new Row(IsFolder: true, "d", FormatTime(folder.CreationTime), "-", name),
            FileNode file => new Row(
                IsFolder: false,
                "-",
                FormatTime(file.ActiveRevision.ClaimedModificationTime?.UtcDateTime ?? file.ActiveRevision.CreationTime),
                FormatSize(file.ActiveRevision.ClaimedSize ?? file.ActiveRevision.StorageSize),
                name),
            _ => new Row(IsFolder: false, "?", FormatTime(node.CreationTime), "-", name),
        };
    }

    private static string FormatTime(DateTime utcTime)
    {
        var local = DateTime.SpecifyKind(utcTime, DateTimeKind.Utc).ToLocalTime();
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private sealed record Row(bool IsFolder, string Type, string Modified, string Size, string Name);
}
