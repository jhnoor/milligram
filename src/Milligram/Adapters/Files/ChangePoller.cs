using System.Diagnostics;
using System.IO.Enumeration;

namespace Milligram.Adapters.Files;

/// <summary>A file's size and modification time: what the poller compares.</summary>
public readonly record struct Stamp(long Length, DateTime Written);

/// <summary>
/// Notices changes by comparing the size and modification time of every watched file, round after round. For where
/// file system events don't arrive. Each round waits ten times as long as the last walk took, so polling a large tree
/// over a slow file system costs about a tenth of one core's time on it, however large it grows.
/// </summary>
public sealed class ChangePoller : IDisposable
{
    private readonly Func<IEnumerable<KeyValuePair<string, Stamp>>> walk;
    private readonly Action<string> changed;
    private readonly TimeSpan minimum;
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;

    public ChangePoller(Func<IEnumerable<KeyValuePair<string, Stamp>>> walk, Action<string> changed, TimeSpan minimum)
    {
        this.walk = walk;
        this.changed = changed;
        this.minimum = minimum;
        loop = Task.Run(RunAsync);
    }

    /// <summary>The wait between rounds, as last measured.</summary>
    public TimeSpan Interval { get; private set; }

    /// <summary>The paths added, removed, or changed in size or modification time.</summary>
    public static IEnumerable<string> Changes(IReadOnlyDictionary<string, Stamp> before, IReadOnlyDictionary<string, Stamp> after) =>
        after.Where(file => !before.TryGetValue(file.Key, out var stamp) || stamp != file.Value).Select(file => file.Key)
            .Concat(before.Keys.Where(path => !after.ContainsKey(path)))
            .Order(StringComparer.Ordinal);

    /// <summary>The files under <paramref name="directory"/> that <paramref name="include"/> accepts, never entering <paramref name="skipped"/> directories.</summary>
    public static IEnumerable<KeyValuePair<string, Stamp>> Files(string directory, Func<string, bool> include, bool recurse, IReadOnlySet<string>? skipped = null)
    {
        if (!Directory.Exists(directory)) return [];
        return new FileSystemEnumerable<KeyValuePair<string, Stamp>>(directory,
            (ref FileSystemEntry entry) => new(entry.ToFullPath(), new Stamp(entry.Length, entry.LastWriteTimeUtc.UtcDateTime)),
            new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true, AttributesToSkip = 0 })
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && include(entry.FileName.ToString()),
            ShouldRecursePredicate = (ref FileSystemEntry entry) => skipped is null || !skipped.Contains(entry.FileName.ToString()),
        };
    }

    private async Task RunAsync()
    {
        var before = Snapshot(out var took);
        while (!stop.IsCancellationRequested)
        {
            Interval = TimeSpan.FromTicks(Math.Max(minimum.Ticks, took.Ticks * 10));
            try { await Task.Delay(Interval, stop.Token); }
            catch (OperationCanceledException) { return; }
            var after = Snapshot(out took);
            if (after is null) continue;
            if (before is not null)
                foreach (var path in Changes(before, after)) changed(path);
            before = after;
        }
    }

    /// <summary>Null when a directory vanished mid-walk; the next round tries again.</summary>
    private Dictionary<string, Stamp>? Snapshot(out TimeSpan took)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var files = new Dictionary<string, Stamp>(StringComparer.Ordinal);
            foreach (var (path, stamp) in walk()) files[path] = stamp;
            return files;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            took = Stopwatch.GetElapsedTime(started);
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        try { loop.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
        stop.Dispose();
    }
}
