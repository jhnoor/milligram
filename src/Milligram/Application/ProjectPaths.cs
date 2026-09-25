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

    public static readonly IReadOnlyList<string> GitIgnored =
        [".milligram/model.json", ".milligram/mail/", ".milligram/run/", ".milligram/agent.md"];

    public string Relative(string path) =>
        Path.GetRelativePath(Root, Path.GetFullPath(path, Root)).Replace('\\', '/');

    public string Absolute(string relative) => Path.GetFullPath(Path.Combine(Root, relative));

    /// <summary>True when <paramref name="path"/> resolves inside the project root (no traversal out).</summary>
    public bool Contains(string path)
    {
        var full = Path.GetFullPath(path, Root);
        var rootWithSlash = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSlash, StringComparison.Ordinal);
    }
}
