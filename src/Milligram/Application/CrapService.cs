using Milligram.Domain.Metrics;

namespace Milligram.Application;

public sealed record CrapResult(int Members, int Files, int TestExitCode);

/// <summary>Runs the tests with coverage and turns line hits plus complexity into a CRAP snapshot.</summary>
public sealed class CrapService(Workspace workspace, IProjectLocator projects, IProcessRunner processes, ICoverageReader coverage)
{
    public async Task<CrapResult> RunAsync(IReadOnlyList<string>? coverageReports, Action<string> log, CancellationToken cancellation)
    {
        var model = workspace.Generate();
        var exitCode = 0;
        var reports = coverageReports;
        if (reports is null or { Count: 0 })
            (reports, exitCode) = await CollectCoverageAsync(log, cancellation);
        if (reports.Count == 0)
            throw new MilligramException("No coverage report was produced. Does the test project reference coverlet.collector?");

        var hits = coverage.Read(reports, workspace.Paths.Root);
        var snapshot = Crap.Compute(model, hits, DateTimeOffset.UtcNow);
        workspace.SaveMetrics(snapshot);
        log($"CRAP: {snapshot.Members.Count} members, {hits.Files.Count} covered files.");
        return new CrapResult(snapshot.Members.Count, hits.Files.Count, exitCode);
    }

    public IReadOnlyList<string> TestProjects()
    {
        var configured = workspace.Policy.Tests.Projects;
        if (configured.Count > 0) return configured.Select(workspace.Paths.Absolute).ToList();
        return projects.Find(workspace.Paths.Root).Where(p => p.IsTest).Select(p => p.Path).ToList();
    }

    private async Task<(IReadOnlyList<string> Reports, int ExitCode)> CollectCoverageAsync(Action<string> log, CancellationToken cancellation)
    {
        var tests = TestProjects();
        if (tests.Count == 0) throw new MilligramException("No test projects found. Set tests.projects in milligram.json.");

        var output = Path.Combine(workspace.Paths.RunDirectory, "coverage", DateTime.UtcNow.ToString("yyyyMMddTHHmmss"));
        var worst = 0;
        foreach (var test in tests)
        {
            log($"dotnet test {workspace.Paths.Relative(test)} (coverage)");
            var args = new List<string> { "test", test, "--collect", "XPlat Code Coverage", "--results-directory", output };
            if (workspace.Policy.Tests.Filter is { Length: > 0 } filter) args.AddRange(["--filter", filter]);
            var code = await processes.RunAsync("dotnet", args, workspace.Paths.Root, log, cancellation);
            if (code != 0) log($"dotnet test exited {code}; using whatever coverage it produced.");
            worst = Math.Max(worst, code);
        }
        var reports = Directory.Exists(output)
            ? Directory.GetFiles(output, "coverage.cobertura.xml", SearchOption.AllDirectories)
            : [];
        return (reports, worst);
    }
}

public sealed class MilligramException(string message) : Exception(message);
