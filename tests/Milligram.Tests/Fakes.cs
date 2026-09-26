using System.Collections.Concurrent;
using Milligram.Application;
using Milligram.Domain.Metrics;

namespace Milligram.Tests;

/// <summary>Records every command and lets a test decide what each one does (e.g. write a report).</summary>
internal sealed class FakeProcessRunner(Func<string, IReadOnlyList<string>, string, int>? onRun = null) : IProcessRunner
{
    public List<(string Command, IReadOnlyList<string> Args, string Directory)> Calls { get; } = [];

    /// <summary>What a command prints, keyed by its first argument (e.g. "--version").</summary>
    public Dictionary<string, string[]> Output { get; } = [];

    public Task<int> RunAsync(string command, IReadOnlyList<string> args, string workingDirectory, Action<string> onLine, CancellationToken cancellation)
    {
        Calls.Add((command, args, workingDirectory));
        onLine($"ran {command} {args.FirstOrDefault()}");
        foreach (var line in Output.GetValueOrDefault(args.FirstOrDefault() ?? "", [])) onLine(line);
        return Task.FromResult(onRun?.Invoke(command, args, workingDirectory) ?? 0);
    }

    public static string After(IReadOnlyList<string> args, string option) => args[args.ToList().IndexOf(option) + 1];

    public static IReadOnlyList<string> AllAfter(IReadOnlyList<string> args, string option) =>
        args.Select((a, i) => (a, i)).Where(x => x.a == option).Select(x => args[x.i + 1]).ToList();
}

internal sealed class FakeProjectLocator(params BuildProject[] projects) : IProjectLocator
{
    /// <summary>Answers <see cref="UsesPackage"/>; by default every project has every package.</summary>
    public Func<BuildProject, string, bool?> Uses { get; init; } = (_, _) => true;

    public IReadOnlyList<BuildProject> Find(string root) => projects;

    public bool? UsesPackage(BuildProject project, string package) => Uses(project, package);
}

internal sealed class FakeCoverageReader(LineHits hits) : ICoverageReader
{
    public List<string> Reports { get; } = [];

    public LineHits Read(IEnumerable<string> reportFiles, string root)
    {
        Reports.AddRange(reportFiles);
        return hits;
    }
}

internal sealed class FakeMutationReader(params Mutant[] mutants) : IMutationReportReader
{
    public IReadOnlyList<Mutant> Read(string reportFile, string root, string projectDirectory) => mutants;
}

internal sealed class FakeCompanion : ICompanion
{
    public bool Available { get; set; } = true;
    public bool Running { get; set; }
    public bool TerminalOpens { get; set; }
    public int Rings { get; private set; }
    public int Starts { get; private set; }

    public string SessionName => "milligram-test";
    public string AttachCommand => "tmux attach -t milligram-test";

    public bool IsAvailable(out string reason)
    {
        reason = Available ? "" : "tmux is not installed.";
        return Available;
    }

    public bool IsRunning() => Running;

    public Task StartAsync(CancellationToken cancellation)
    {
        Starts++;
        Running = true;
        return Task.CompletedTask;
    }

    public void Stop() => Running = false;
    public void Ring() => Rings++;
    public bool OpenTerminal() => TerminalOpens;
}

internal sealed class FakeEvents : IViewerEvents
{
    private readonly ConcurrentQueue<(string Type, object? Payload)> published = new();

    public IReadOnlyList<string> Types => published.Select(p => p.Type).ToList();

    public void Publish(string type, object? payload = null) => published.Enqueue((type, payload));
}

internal sealed class FakeWatchLimits(WatchLimit? limit = null) : IWatchLimits
{
    public WatchLimit? For(string root) => limit;
}

internal static class Jobs
{
    /// <summary>Waits for the named job to start, rather than sleeping and hoping it has.</summary>
    public static async Task Running(JobQueue jobs, string name)
    {
        for (var i = 0; i < 400; i++)
        {
            if (jobs.Status is { State: JobState.Running } status && status.Name == name) return;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Job '{name}' did not start; last status {jobs.Status}.");
    }

    /// <summary>Waits for the named job to finish and returns its final status.</summary>
    public static async Task<JobStatus> Finished(JobQueue jobs, string name)
    {
        for (var i = 0; i < 400; i++)
        {
            var status = jobs.Status;
            if (status.Name == name && status.State is JobState.Succeeded or JobState.Failed) return status;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Job '{name}' did not finish; last status {jobs.Status}.");
    }
}
