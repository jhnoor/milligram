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
        var current = boundary ?? Volume(path);
        var remaining = new Queue<string>(Segments(path[current.Length..]));
        while (remaining.TryDequeue(out var part))
        {
            current = Path.GetFullPath(Path.Combine(current, part));
            if (boundary is not null && !AtOrWithin(current, boundary)) return null;
            if (OperatingSystem.IsWindows()) current = StoredPath(current);
            var target = LinkTarget(current);
            if (target is null) continue;
            if (++links > 64) return null;
            if (boundary is not null)
            {
                if (alias is not null && AtOrWithin(target, alias)) target = boundary + target[alias.Length..];
                if (!AtOrWithin(target, boundary)) target = ResolveAncestorAlias(target, boundary, ref links);
                if (target is null) return null;
            }
            current = boundary ?? Volume(target);
            remaining = new Queue<string>(Segments(target[current.Length..]).Concat(remaining));
        }
        return current;
    }

    /// <summary>An ancestor alias such as macOS /var may name the physical root; never descend into an outside ordinary directory.</summary>
    private static string? ResolveAncestorAlias(string path, string boundary, ref int links)
    {
        var volume = Volume(boundary);
        if (!string.Equals(Volume(path), volume, StringComparison.Ordinal)) return null;
        var current = volume;
        var remaining = new Queue<string>(Segments(path[volume.Length..]));
        while (remaining.TryDequeue(out var part))
        {
            current = Path.GetFullPath(Path.Combine(current, part));
            if (OperatingSystem.IsWindows()) current = StoredPath(current);
            var target = LinkTarget(current);
            if (target is not null)
            {
                if (++links > 64 || !string.Equals(Volume(target), volume, StringComparison.Ordinal)) return null;
                current = volume;
                remaining = new Queue<string>(Segments(target[volume.Length..]).Concat(remaining));
            }
            else if (AtOrWithin(current, boundary)) return Path.Join(current, string.Join(Path.DirectorySeparatorChar, remaining));
            else if (!AtOrWithin(boundary, current)) return null;
        }
        return null;
    }

    private static string? LinkTarget(string path)
    {
        try { return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: false)?.ToString(); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private static string Volume(string path)
    {
        var volume = Path.GetPathRoot(path)!;
        return OperatingSystem.IsWindows() && volume.Length == 3 && volume[1] == ':' ? volume.ToUpperInvariant() : volume;
    }

    /// <summary>Normalize one existing component without following it; exact names keep case-sensitive siblings distinct.</summary>
    internal static string StoredPath(string path)
    {
        try
        {
            var entries = Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!, Path.GetFileName(path), new EnumerationOptions
            {
                MatchCasing = MatchCasing.CaseInsensitive,
                MatchType = MatchType.Simple,
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
            });
            return entries.FirstOrDefault(entry => string.Equals(entry, path, StringComparison.Ordinal)) ?? entries.Length switch
            {
                0 => path,
                1 => entries[0],
                _ => throw new IOException("Ambiguous directory-entry casing."),
            };
        }
        catch (DirectoryNotFoundException) { return path; }
    }

    private static string[] Segments(string path) =>
        path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
}
