namespace Milligram.Application;

/// <summary>Where Milligram keeps its files inside an examined project.</summary>
public sealed class ProjectPaths(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string PolicyFile => Path.Combine(Root, "milligram.json");
    public string StateDirectory => Path.Combine(Root, ".milligram");
    public string ModelFile => Path.Combine(StateDirectory, "model.json");
    public string MetricsDirectory => Path.Combine(StateDirectory, "metrics");
    public string CrapFile => Path.Combine(MetricsDirectory, "crap.json");
    public string MutationFile => Path.Combine(MetricsDirectory, "mutation.json");
    public string MailDirectory => Path.Combine(StateDirectory, "mail");
    public string ToAgentDirectory => Path.Combine(MailDirectory, "to-agent");
    public string ToViewerDirectory => Path.Combine(MailDirectory, "to-viewer");
    public string RunDirectory => Path.Combine(StateDirectory, "run");
    public string BriefingFile => Path.Combine(StateDirectory, "agent.md");
    public string AgentStateFile => Path.Combine(RunDirectory, "agent.json");
    public string ServerFile => Path.Combine(RunDirectory, "server.json");

    /// <summary>Everything Milligram writes is local: the model and metrics regenerate, mail and run files are transient.</summary>
    public static readonly IReadOnlyList<string> GitIgnored = [".milligram/"];

    public string Relative(string path) =>
        Path.GetRelativePath(Root, Path.GetFullPath(path, Root)).Replace('\\', '/');

    public string Absolute(string relative) => Path.GetFullPath(Path.Combine(Root, relative));

    /// <summary>Neither parent traversal nor a nested filesystem link may leave the chosen project root.</summary>
    public bool Contains(string path)
    {
        var full = Path.GetFullPath(path, Root);
        if (!Within(full, Root)) return false;
        try
        {
            var links = 0;
            var physicalRoot = ResolveLinks(Root, null, null, ref links);
            return physicalRoot is not null && ResolveLinks(Path.Combine(physicalRoot, Path.GetRelativePath(Root, full)),
                physicalRoot, Path.TrimEndingDirectorySeparator(Root), ref links) is not null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool Within(string path, string root) =>
        path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static bool AtOrWithin(string path, string root) => string.Equals(path, root, StringComparison.Ordinal) || Within(path, root);

    /// <summary>Read immediate targets so a link is resolved before a following '..'; reject outside targets before opening them.</summary>
    private static string? ResolveLinks(string path, string? boundary, string? alias, ref int links)
    {
        var current = boundary ?? Path.GetPathRoot(path)!;
        var remaining = new Queue<string>(Segments(path[current.Length..]));
        while (remaining.TryDequeue(out var part))
        {
            current = Path.GetFullPath(Path.Combine(current, part));
            if (boundary is not null && !AtOrWithin(current, boundary)) return null;
            string? target = null;
            try { target = new FileInfo(current).ResolveLinkTarget(returnFinalTarget: false)?.ToString(); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { continue; }
            if (target is null) continue;
            if (++links > 64) return null;
            if (boundary is not null)
            {
                if (alias is not null && AtOrWithin(target, alias)) target = boundary + target[alias.Length..];
                if (!AtOrWithin(target, boundary)) return null;
            }
            current = boundary ?? Path.GetPathRoot(target)!;
            remaining = new Queue<string>(Segments(target[current.Length..]).Concat(remaining));
        }
        return current;
    }

    private static string[] Segments(string path) =>
        path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
}
