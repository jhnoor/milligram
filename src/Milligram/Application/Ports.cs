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

public sealed record BuildProject(string Path, string Name, bool IsTest, IReadOnlyList<string> References)
{
    public string Directory => System.IO.Path.GetDirectoryName(Path)!;
}

/// <summary>Finds buildable projects (and which are tests) under a root.</summary>
public interface IProjectLocator
{
    IReadOnlyList<BuildProject> Find(string root);
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
