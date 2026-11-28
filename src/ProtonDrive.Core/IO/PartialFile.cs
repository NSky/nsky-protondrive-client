namespace NSkyLabs.ProtonDrive.Core.IO;

/// <summary>
/// Creates the file a download is written to before it is renamed into place.
/// </summary>
/// <remarks>
/// The file is always created new (<c>O_CREAT | O_EXCL</c>), which fails instead of following a symbolic link. A
/// leftover file or link at the path (an interrupted download, or one planted by another user who can write to the
/// folder) is removed first: deleting a link removes the link, never what it points to. If something appears at the
/// path again in between, creating the file fails rather than writing through it.
/// </remarks>
internal static class PartialFile
{
    public static FileStream Create(string path)
    {
        File.Delete(path);

        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        });
    }
}
