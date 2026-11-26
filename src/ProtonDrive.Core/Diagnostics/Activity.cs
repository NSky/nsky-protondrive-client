using System.Diagnostics;

namespace NSkyLabs.ProtonDrive.Core.Diagnostics;

/// <summary>
/// What the program is doing right now, for a status line. Long-running steps (scanning folders, listing the
/// remote tree, saving state) report here; the console app decides how to show it. Reporting is cheap (no I/O),
/// so it can be done for every item.
/// </summary>
/// <example>
/// <code>
/// using var step = Activity.Begin("Scanning the local folder", "items");
/// foreach (var entry in entries) { step.Increment(); }
/// </code>
/// </example>
internal static class Activity
{
    private static ActivityStep? _current;

    /// <summary>The innermost step in progress, or <see langword="null"/> when idle.</summary>
    public static ActivityStep? Current => Volatile.Read(ref _current);

    /// <summary>Starts a step; it ends when disposed. Steps nest: the previous one is shown again afterwards.</summary>
    /// <param name="description">What is being done, e.g. "Listing the remote folder".</param>
    /// <param name="unit">What <see cref="ActivityStep.Increment"/> counts, e.g. "items"; null for no counter.</param>
    public static ActivityStep Begin(string description, string? unit = null)
    {
        var step = new ActivityStep(description, unit, Current);
        Volatile.Write(ref _current, step);
        return step;
    }

    internal static void End(ActivityStep step)
    {
        Interlocked.CompareExchange(ref _current, step.Parent, step);
    }
}

/// <summary>One step of <see cref="Activity"/>. Thread-safe.</summary>
internal sealed class ActivityStep : IDisposable
{
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private long _count;
    private long _total = -1;
    private string? _detail;

    internal ActivityStep(string description, string? unit, ActivityStep? parent)
    {
        Description = description;
        Unit = unit;
        Parent = parent;
    }

    public string Description { get; }

    public string? Unit { get; }

    public ActivityStep? Parent { get; }

    public long Count => Interlocked.Read(ref _count);

    /// <summary>The expected count, or -1 if unknown.</summary>
    public long Total => Interlocked.Read(ref _total);

    /// <summary>The item being worked on, e.g. a folder path.</summary>
    public string? Detail => Volatile.Read(ref _detail);

    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_startedAt);

    /// <summary>The outermost step this one is part of.</summary>
    public ActivityStep Root
    {
        get
        {
            var step = this;
            while (step.Parent is not null)
            {
                step = step.Parent;
            }

            return step;
        }
    }

    public void Increment(long by = 1) => Interlocked.Add(ref _count, by);

    public void SetTotal(long total) => Interlocked.Exchange(ref _total, total);

    public void SetDetail(string? detail) => Volatile.Write(ref _detail, detail);

    public void Dispose() => Activity.End(this);
}
