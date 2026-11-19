namespace NskyProtonDrive.Cli.Drive;

/// <summary>
/// A path inside "My files", normalized to its segments. Both "docs/a" and "/docs/a" are relative to the
/// Drive root; "." and ".." are resolved lexically and cannot go above the root.
/// </summary>
internal sealed class RemotePath
{
    private RemotePath(IReadOnlyList<string> segments)
    {
        Segments = segments;
    }

    public static RemotePath Root { get; } = new([]);

    public IReadOnlyList<string> Segments { get; }

    public bool IsRoot => Segments.Count == 0;

    public static RemotePath Parse(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Root;
        }

        var segments = new List<string>();

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (segment)
            {
                case ".":
                    break;

                case "..":
                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }

                    break;

                default:
                    segments.Add(segment);
                    break;
            }
        }

        return segments.Count == 0 ? Root : new RemotePath(segments);
    }

    public override string ToString() => "/" + string.Join('/', Segments);
}
