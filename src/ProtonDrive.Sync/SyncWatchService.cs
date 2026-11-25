namespace NSkyLabs.ProtonDrive.Sync;

internal sealed record SyncWatchOptions
{
    /// <summary>How often the server's events feed is polled (the SDK has no push).</summary>
    public TimeSpan RemotePollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long local changes must settle before a pass starts (editors save in several steps).</summary>
    public TimeSpan LocalQuietPeriod { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>A full local scan runs at least this often, in case file events were missed.</summary>
    public TimeSpan FullScanInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Wait after a failed pass before trying that pair again.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Watch mode: keeps every pair in sync until cancelled. A pass runs when local files change (after a quiet period),
/// when the server reports changes, and on a regular full-scan interval. Each run is an ordinary sync pass.
/// </summary>
internal sealed class SyncWatchService(SyncEngine engine, IReadOnlyList<SyncPair> pairs, SyncWatchOptions options, ISyncObserver observer)
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    /// <summary>Runs until cancelled. Throws only for errors that retrying cannot fix (e.g. the session ended).</summary>
    public async Task RunAsync(Func<Exception, bool> isFatal, CancellationToken cancellationToken)
    {
        var states = pairs.Select(p => new PairState(p)).ToList();

        try
        {
            foreach (var state in states)
            {
                state.StartWatching();
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;

                foreach (var state in states)
                {
                    if (now < state.NotBefore)
                    {
                        continue;
                    }

                    var localDue = state.IsLocalDirty && now - state.LastLocalChange >= options.LocalQuietPeriod;
                    var fullDue = now >= state.NextFullScan;
                    var remoteDue = now >= state.NextRemotePoll;

                    if (!localDue && !fullDue && !remoteDue)
                    {
                        continue;
                    }

                    // Events that arrive during the pass (including from its own downloads) mark the pair dirty again;
                    // the next pass then finds nothing to do.
                    state.ClearLocalDirty();

                    try
                    {
                        await engine.RunAsync(
                                state.Pair,
                                new SyncPassOptions { LocalMayHaveChanged = localDue || fullDue || state.WatcherFailed },
                                cancellationToken)
                            .ConfigureAwait(false);

                        state.NextRemotePoll = DateTime.UtcNow + options.RemotePollInterval;
                        if (fullDue || state.WatcherFailed)
                        {
                            state.NextFullScan = DateTime.UtcNow + options.FullScanInterval;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        if (isFatal(ex))
                        {
                            throw;
                        }

                        observer.Warning(state.Pair, $"Sync failed, retrying in {options.RetryDelay.TotalSeconds:0}s: {ex.Message}");
                        state.NotBefore = DateTime.UtcNow + options.RetryDelay;
                        state.MarkLocalDirty();
                    }
                }

                await Task.Delay(Tick, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped.
        }
        finally
        {
            foreach (var state in states)
            {
                state.Dispose();
            }
        }
    }

    private sealed class PairState(SyncPair pair) : IDisposable
    {
        private FileSystemWatcher? _watcher;
        private int _dirty = 1;
        private long _lastChangeTicks;

        public SyncPair Pair => pair;

        public bool IsLocalDirty => Volatile.Read(ref _dirty) != 0;

        public DateTime LastLocalChange => new(Interlocked.Read(ref _lastChangeTicks), DateTimeKind.Utc);

        public DateTime NextRemotePoll { get; set; }

        public DateTime NextFullScan { get; set; }

        public DateTime NotBefore { get; set; }

        /// <summary>File events cannot be trusted (watch limit reached or buffer overflow): rescan on every pass.</summary>
        public bool WatcherFailed { get; private set; }

        public void StartWatching()
        {
            try
            {
                _watcher = new FileSystemWatcher(pair.LocalPath)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };

                _watcher.Changed += OnChanged;
                _watcher.Created += OnChanged;
                _watcher.Deleted += OnChanged;
                _watcher.Renamed += OnChanged;
                _watcher.Error += (_, _) =>
                {
                    WatcherFailed = true;
                    MarkLocalDirty();
                };
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
            {
                WatcherFailed = true;
            }
        }

        public void MarkLocalDirty()
        {
            Interlocked.Exchange(ref _lastChangeTicks, DateTime.UtcNow.Ticks);
            Volatile.Write(ref _dirty, 1);
        }

        public void ClearLocalDirty() => Volatile.Write(ref _dirty, 0);

        public void Dispose() => _watcher?.Dispose();

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            // The engine's own files (temporary downloads, local trash, the notice file) are not synced.
            var relative = Path.GetRelativePath(pair.LocalPath, e.FullPath);
            var top = relative.Split(Path.DirectorySeparatorChar)[0];
            if (SyncPaths.IsEngineEntry(top) || top.StartsWith(SyncAttention.NoticeFileName, StringComparison.Ordinal))
            {
                return;
            }

            MarkLocalDirty();
        }
    }
}
