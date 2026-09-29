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
    /// <summary>Directories excluded from source; project inputs under obj are watched separately.</summary>
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
        Watch(paths.Root, ["milligram.json"], recursive: false);
        Watch(paths.StateDirectory, ["*"], recursive: true);
        Watch(paths.Root, ["*.cs", "*.csproj", "project.assets.json"], recursive: true);
        Watch(paths.Root, ["*"], recursive: true, directories: true);
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

    /// <summary>Polling includes the same project inputs that can change a native event-driven scan.</summary>
    internal IEnumerable<KeyValuePair<string, Stamp>> Watched() =>
        ChangePoller.Files(paths.Absolute(workspace.Policy.Src), name => name.EndsWith(".cs", StringComparison.Ordinal), recurse: true, Unscanned)
            .Concat(ProjectInputs())
            .Concat(ChangePoller.Files(paths.Root, name => name == "milligram.json", recurse: false))
            .Concat(ChangePoller.Files(paths.StateDirectory, name => name == "model.json", recurse: false))
            .Concat(ChangePoller.Files(paths.MetricsDirectory, _ => true, recurse: true))
            .Concat(ChangePoller.Files(paths.ToViewerDirectory, _ => true, recurse: false));

    private IEnumerable<KeyValuePair<string, Stamp>> ProjectInputs()
    {
        var projects = ChangePoller.Files(paths.Root, name => name.EndsWith(".csproj", StringComparison.Ordinal), recurse: true, Unscanned)
            .Where(file => IsProjectInput(paths.Relative(file.Key))).ToList();
        foreach (var file in projects) yield return file;
        foreach (var directory in projects.Select(file => Path.GetDirectoryName(file.Key)!).Distinct(StringComparer.Ordinal))
            foreach (var file in ChangePoller.Files(Path.Combine(directory, "obj"),
                name => name == "project.assets.json" || name.EndsWith(".GlobalUsings.g.cs", StringComparison.Ordinal), recurse: true))
                if (IsProjectInput(paths.Relative(file.Key))) yield return file;
    }

    /// <summary>When changes came too fast to list, everything they could have touched is refreshed.</summary>
    internal void RefreshAll()
    {
        debounce.Run("policy", 300, PolicyChanged);
        debounce.Run("model", 300, ModelChanged);
        debounce.Run("metrics", 300, MetricsChanged);
        debounce.Run("mail", 150, DeliverMail);
        debounce.Run("source", 800, SourceChanged);
    }

    private void Watch(string directory, IReadOnlyList<string> filters, bool recursive, bool directories = false)
    {
        var watcher = new FileSystemWatcher(directory, filters[0])
        {
            IncludeSubdirectories = recursive,
            NotifyFilter = directories ? NotifyFilters.DirectoryName : NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        foreach (var filter in filters.Skip(1)) watcher.Filters.Add(filter);
        Action<string> changed = directories ? DirectoryChanged : OnChange;
        watcher.Changed += (_, e) => changed(e.FullPath);
        watcher.Created += (_, e) => changed(e.FullPath);
        watcher.Deleted += (_, e) => changed(e.FullPath);
        watcher.Renamed += (_, e) => { changed(e.OldFullPath); changed(e.FullPath); };
        watcher.Error += (_, _) => RefreshAll();
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
        else if ((relative.EndsWith(".cs", StringComparison.Ordinal) && IsScanned(relative)) || IsProjectInput(relative))
            debounce.Run("source", 800, SourceChanged);
        else if (IsProjectInput(relative + "/project.assets.json") || (IsScanned(relative, directory: true) && (Directory.Exists(fullPath) ||
            workspace.Model.Types.Any(type => type.Spans.Any(span => span.File.StartsWith(relative + "/", StringComparison.Ordinal))))))
            DirectoryChanged(fullPath);
    }

    /// <summary>A folder move may raise no events for the source files it carries.</summary>
    private void DirectoryChanged(string fullPath)
    {
        var relative = paths.Relative(fullPath);
        if (IsScanned(relative, directory: true) || IsProjectInput(relative + "/project.assets.json"))
            debounce.Run("source", 800, SourceChanged);
    }

    internal bool IsProjectInput(string relative)
    {
        var parts = relative.Split('/');
        var ownerLength = parts.Length - 1;
        if (!relative.EndsWith(".csproj", StringComparison.Ordinal))
        {
            ownerLength = Array.IndexOf(parts, "obj");
            if (ownerLength < 0 || !((parts.Length == ownerLength + 2 && parts[^1] == "project.assets.json") ||
                parts[^1].EndsWith(".GlobalUsings.g.cs", StringComparison.Ordinal))) return false;
        }
        var owner = ownerLength == 0 ? "." : string.Join('/', parts.Take(ownerLength));
        return IsScanned(owner, directory: true);
    }

    internal bool IsScanned(string relative, bool directory = false)
    {
        var policy = workspace.Policy;
        var src = paths.Relative(paths.Absolute(policy.Src)).TrimEnd('/');
        if (src != "." && !relative.StartsWith(src + "/", StringComparison.Ordinal) &&
            !(directory && (relative == "." || relative == src || src.StartsWith(relative + "/", StringComparison.Ordinal)))) return false;
        if (relative.Split('/').Any(Unscanned.Contains)) return false;
        return !policy.Exclude.Any(glob => Glob.Matches(glob, directory ? relative + "/" : relative));
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
