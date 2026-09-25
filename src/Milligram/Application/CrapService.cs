using Milligram.Domain.Metrics;

namespace Milligram.Application;

public sealed record CrapResult(int Members, int Files, int TestExitCode);

/// <summary>Runs the tests with coverage and turns line hits plus complexity into a CRAP snapshot.</summary>
public sealed class CrapService(Workspace workspace, IProjectLocator projects, IProcessRunner processes, ICoverageReader coverage)
{
    /// <summary>`dotnet test --collect "XPlat Code Coverage"` needs this package in each test project.</summary>
    public const string CoverageCollector = "coverlet.collector";

    public async Task<CrapResult> RunAsync(IReadOnlyList<string>? coverageReports, Action<string> log, CancellationToken cancellation)
    {
        var collect = coverageReports is null or { Count: 0 };
        var tests = collect ? RunnableTestProjects(log) : [];
        var model = workspace.Generate();
        var (reports, exitCode) = collect ? await CollectCoverageAsync(tests, log, cancellation) : (coverageReports!, 0);
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

    /// <summary>The test projects to run, checked before the scan so that a missing prerequisite fails in seconds.</summary>
    private IReadOnlyList<string> RunnableTestProjects(Action<string> log)
    {
        var tests = TestProjects();
        if (tests.Count == 0) throw new MilligramException("No test projects found. Set tests.projects in milligram.json.");
        RequireCoverageCollector(tests, log);
        return tests;
    }

    private async Task<(IReadOnlyList<string> Reports, int ExitCode)> CollectCoverageAsync(IReadOnlyList<string> tests, Action<string> log, CancellationToken cancellation)
    {
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

    /// <summary>
    /// Fails before running any tests when no test project can produce coverage, and warns about those that
    /// can't. A project that is not restored yet is given the benefit of the doubt: `dotnet test` restores it.
    /// </summary>
    private void RequireCoverageCollector(IReadOnlyList<string> tests, Action<string> log)
    {
        var found = projects.Find(workspace.Paths.Root).ToDictionary(p => p.Path, StringComparer.Ordinal);
        var lacking = tests.Where(t => found.TryGetValue(t, out var p) && projects.UsesPackage(p, CoverageCollector) == false).ToList();
        var fixes = string.Join("; ", lacking.Select(t => $"dotnet add {workspace.Paths.Relative(t)} package {CoverageCollector}"));
        if (lacking.Count == tests.Count)
            throw new MilligramException($"No test project can measure coverage: they lack {CoverageCollector}. Fix: {fixes}");
        foreach (var test in lacking)
            log($"{workspace.Paths.Relative(test)} lacks {CoverageCollector}, so its tests add no coverage. Fix: dotnet add {workspace.Paths.Relative(test)} package {CoverageCollector}");
    }
}

public sealed class MilligramException(string message) : Exception(message);
