using Milligram.Domain.Metrics;
using Milligram.Domain.Model;

namespace Milligram.Application;

public sealed record ScanRequest(
    string Root,
    string SourceDirectory,
    IReadOnlyList<string> Exclude,
    string Prefix,
    IReadOnlyList<string> Foreign,
    string Title);

/// <summary>Reads a source tree and emits the topology: types, members, and dependencies.</summary>
public interface ILanguageScanner
{
    CodeModel Scan(ScanRequest request);
}

/// <summary>Reads coverage reports into project-relative line hits.</summary>
public interface ICoverageReader
{
    LineHits Read(IEnumerable<string> reportFiles, string root);
}

/// <summary>Reads a mutation report into project-relative mutants.</summary>
public interface IMutationReportReader
{
    IReadOnlyList<Mutant> Read(string reportFile, string root, string projectDirectory);
}

/// <summary>A project file; <see cref="IsRestored"/> means its packages resolve, so the scanner can bind their types.</summary>
public sealed record BuildProject(string Path, string Name, bool IsTest, IReadOnlyList<string> References, bool IsRestored = false)
{
    public string Directory => System.IO.Path.GetDirectoryName(Path)!;
}

/// <summary>Finds buildable projects (and which are tests) under a root.</summary>
public interface IProjectLocator
{
    IReadOnlyList<BuildProject> Find(string root);

    /// <summary>
    /// Whether <paramref name="project"/> uses <paramref name="package"/>: true when its project file names it; otherwise
    /// what its restored packages say, or null before a restore, when there is no way to tell.
    /// </summary>
    bool? UsesPackage(BuildProject project, string package);
}

public interface IProcessRunner
{
    Task<int> RunAsync(string command, IReadOnlyList<string> args, string workingDirectory, Action<string> onLine, CancellationToken cancellation);
}

/// <summary>The agent session that works alongside the viewer.</summary>
public interface ICompanion
{
    string SessionName { get; }
    string AttachCommand { get; }
    bool IsAvailable(out string reason);
    bool IsRunning();
    Task StartAsync(CancellationToken cancellation);
    void Stop();
    /// <summary>Wakes the agent to read its mail. The doorbell carries no content.</summary>
    void Ring();
    bool OpenTerminal();
}

/// <summary>Push notifications to connected viewers.</summary>
public interface IViewerEvents
{
    void Publish(string type, object? payload = null);
}
