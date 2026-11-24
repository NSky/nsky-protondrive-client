namespace NSkyLabs.ProtonDrive.Core.IO;

/// <summary>
/// An exclusive lock shared between processes, held by keeping a lock file open.
/// </summary>
/// <remarks>
/// On Unix, .NET backs <see cref="FileShare.None"/> with an advisory <c>flock</c>, so a second process (or a second
/// handle in this process) cannot open the file until the first handle is closed. The lock is released by the OS
/// if the process dies.
/// </remarks>
internal sealed class FileLock : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly FileStream _stream;

    private FileLock(FileStream stream)
    {
        _stream = stream;
    }

    /// <summary>Takes the lock, or returns <see langword="null"/> at once if another holder has it.</summary>
    public static FileLock? TryAcquire(string path)
    {
        PrivateDirectory.Ensure(Path.GetDirectoryName(Path.GetFullPath(path))!);

        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
            };

            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            return new FileLock(new FileStream(path, options));
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Waits for the lock until <paramref name="timeout"/> elapses, then throws <see cref="TimeoutException"/>.</summary>
    public static async Task<FileLock> AcquireAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            if (TryAcquire(path) is { } fileLock)
            {
                return fileLock;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out waiting for the lock {path}.");
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Synchronous variant of <see cref="AcquireAsync"/>.</summary>
    public static FileLock Acquire(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            if (TryAcquire(path) is { } fileLock)
            {
                return fileLock;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out waiting for the lock {path}.");
            }

            Thread.Sleep(PollInterval);
        }
    }

    public void Dispose() => _stream.Dispose();
}
