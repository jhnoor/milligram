using Milligram.Application;
using Milligram.Domain.Policies;

namespace Milligram.Adapters.Files;

/// <summary>
/// Watches the examined project and keeps the viewer live: source and policy changes regenerate the
/// model, metric snapshots reload, and mail from the agent is delivered to the browser. On a Windows drive
/// under WSL, where file system events miss what Windows programs change, it also watches from the Windows
/// side, or polls when it can't.
/// </summary>
public sealed class ProjectWatcher : IDisposable
{
    /// <summary>Directories that are never scanned, so neither events from them nor polling them matter.</summary>
    private static readonly HashSet<string> Unscanned = ["bin", "obj", ".git", ".milligram", "node_modules", ".vs", ".idea"];

    private readonly ProjectPaths paths;
    private readonly Workspace workspace;
    private readonly ViewerActions actions;
    private readonly IViewerEvents events;
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly Debouncer debounce = new();
    private readonly WindowsSideWatcher? windowsSide;
    private readonly ChangePoller? poller;

    public ProjectWatcher(ProjectPaths paths, Workspace workspace, ViewerActions actions, IViewerEvents events, bool windowsDrive = false)
    {
        this.paths = paths;
        this.workspace = workspace;
        this.actions = actions;
        this.events = events;
        Directory.CreateDirectory(paths.ToViewerDirectory);
        Directory.CreateDirectory(paths.MetricsDirectory);
        Watch(paths.Root, "milligram.json", recursive: false);
        Watch(paths.StateDirectory, "*", recursive: true);
        Watch(paths.Root, "*.cs", recursive: true);
        if (windowsDrive)
        {
            windowsSide = WindowsSideWatcher.Start(paths.Root, OnChange, RefreshAll, TimeSpan.FromSeconds(15));
            if (windowsSide is null) poller = new ChangePoller(Watched, OnChange, TimeSpan.FromSeconds(2));
        }
        DeliverMail();
    }

    /// <summary>How changes made outside Linux reach the viewer, for the banner; null when events see them all.</summary>
    public string? Workaround =>
        windowsSide is not null ? "also watches it from Windows, through powershell.exe"
        : poller is not null ? "polls for them instead, every few seconds or more on a large project (powershell.exe didn't start)"
        : null;

    /// <summary>What polling looks at: the scanned source, the policy, the model, the metrics, and mail for the viewer.</summary>
    private IEnumerable<KeyValuePair<string, Stamp>> Watched() =>
        ChangePoller.Files(paths.Absolute(workspace.Policy.Src), name => name.EndsWith(".cs", StringComparison.Ordinal), recurse: true, Unscanned)
            .Concat(ChangePoller.Files(paths.Root, name => name == "milligram.json", recurse: false))
            .Concat(ChangePoller.Files(paths.StateDirectory, name => name == "model.json", recurse: false))
            .Concat(ChangePoller.Files(paths.MetricsDirectory, _ => true, recurse: true))
            .Concat(ChangePoller.Files(paths.ToViewerDirectory, _ => true, recurse: false));

    /// <summary>When changes came too fast to list, everything they could have touched is refreshed.</summary>
    private void RefreshAll()
    {
        debounce.Run("policy", 300, PolicyChanged);
        debounce.Run("model", 300, ModelChanged);
        debounce.Run("metrics", 300, MetricsChanged);
        debounce.Run("mail", 150, DeliverMail);
        debounce.Run("source", 800, SourceChanged);
    }

    private void Watch(string directory, string filter, bool recursive)
    {
        var watcher = new FileSystemWatcher(directory, filter)
        {
            IncludeSubdirectories = recursive,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Changed += (_, e) => OnChange(e.FullPath);
        watcher.Created += (_, e) => OnChange(e.FullPath);
        watcher.Deleted += (_, e) => OnChange(e.FullPath);
        watcher.Renamed += (_, e) => { OnChange(e.OldFullPath); OnChange(e.FullPath); };
        watcher.EnableRaisingEvents = true;
        watchers.Add(watcher);
    }

    private void OnChange(string fullPath)
    {
        var relative = paths.Relative(fullPath);
        if (relative.EndsWith(".tmp", StringComparison.Ordinal)) return;
        if (relative == "milligram.json") debounce.Run("policy", 300, PolicyChanged);
        else if (relative == ".milligram/model.json") debounce.Run("model", 300, ModelChanged);
        else if (relative.StartsWith(".milligram/metrics/", StringComparison.Ordinal)) debounce.Run("metrics", 300, MetricsChanged);
        else if (relative.StartsWith(".milligram/mail/to-viewer/", StringComparison.Ordinal)) debounce.Run("mail", 150, DeliverMail);
        else if (relative.EndsWith(".cs", StringComparison.Ordinal) && IsScanned(relative)) debounce.Run("source", 800, SourceChanged);
    }

    private bool IsScanned(string relative)
    {
        var policy = workspace.Policy;
        var src = policy.Src.Trim('/').Replace('\\', '/');
        if (src is not ("." or "") && !relative.StartsWith(src + "/", StringComparison.Ordinal)) return false;
        if (relative.Split('/').Any(Unscanned.Contains)) return false;
        return !policy.Exclude.Any(glob => Glob.Matches(glob, relative));
    }

    private void PolicyChanged()
    {
        if (workspace.ReloadPolicy()) actions.Regenerate("Scan (milligram.json changed)");
        events.Publish("policy");
    }

    private void ModelChanged()
    {
        workspace.ReloadModel();
        events.Publish("model");
    }

    private void MetricsChanged()
    {
        workspace.ReloadMetrics();
        events.Publish("metrics");
    }

    private void SourceChanged() => actions.Regenerate("Scan (source changed)");

    private void DeliverMail()
    {
        foreach (var message in workspace.ToViewer.Take()) events.Publish("mail", message);
    }

    public void Dispose()
    {
        windowsSide?.Dispose();
        poller?.Dispose();
        foreach (var watcher in watchers) watcher.Dispose();
        debounce.Dispose();
    }

    private sealed class Debouncer : IDisposable
    {
        private readonly Lock gate = new();
        private readonly Dictionary<string, Timer> timers = [];

        public void Run(string key, int milliseconds, Action action)
        {
            lock (gate)
            {
                if (timers.TryGetValue(key, out var existing)) existing.Change(milliseconds, Timeout.Infinite);
                else timers[key] = new Timer(_ => Fire(key, action), null, milliseconds, Timeout.Infinite);
            }
        }

        private void Fire(string key, Action action)
        {
            lock (gate)
            {
                if (timers.Remove(key, out var timer)) timer.Dispose();
            }
            try { action(); }
            catch (Exception e) { Console.Error.WriteLine($"milligram: {key} refresh failed: {e.Message}"); }
        }

        public void Dispose()
        {
            lock (gate)
            {
                foreach (var timer in timers.Values) timer.Dispose();
                timers.Clear();
            }
        }
    }
}
