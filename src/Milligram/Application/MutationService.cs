using Milligram.Domain.Metrics;
using Milligram.Domain.Model;

namespace Milligram.Application;

public sealed record MutationResult(int Projects, int Members, int Mutants, IReadOnlyList<string> Skipped);

/// <summary>
/// Runs Stryker.NET on source files. By default only members whose code changed since their last
/// mutation run are mutated (differential); <c>all</c> mutates every member of the files.
/// </summary>
public sealed class MutationService(Workspace workspace, IProjectLocator locator, IProcessRunner processes, IMutationReportReader reports,
    IMetricProjectReader? metricProjects = null)
{
    public async Task<MutationResult> RunAsync(IReadOnlyList<string> files, bool all, Action<string> log, CancellationToken cancellation)
    {
        var model = workspace.Generate(log);
        var projects = locator.Find(workspace.Paths.Root);
        var targets = files.Count > 0 ? files.Select(Normalize).ToList() : model.Types.SelectMany(t => t.Files).Distinct().ToList();
        var skipped = new List<string>();
        int projectCount = 0, memberCount = 0, mutantCount = 0;
        var testPaths = workspace.Policy.Tests.Projects.Count > 0 ? workspace.Policy.Tests.Projects.Select(workspace.Paths.Absolute).ToList()
            : projects.Where(project => project.IsTest).Select(project => project.Path).ToList();
        var evaluated = model.Types.Any(type => type.Context is not null) && metricProjects is not null
            ? await metricProjects.ReadAsync(workspace.Paths.Root, testPaths, workspace.ScanSettings.Configuration, log, cancellation) : [];

        foreach (var group in Targets(model, targets, projects))
        {
            var project = group.Project;
            if (project is null || group.Context?.Resolved == false)
            {
                skipped.AddRange(group.Files);
                log($"Cannot establish project ownership for: {string.Join(", ", group.Files)}; skipping.");
                continue;
            }
            if (project.IsTest) continue;
            var label = group.Context?.Label ?? project.Name;
            var tests = group.Context is { } context ? MutationTests.Select(context, evaluated)
                .Select(test => workspace.Paths.Absolute(test.Project)).ToList()
                : projects.Where(p => testPaths.Contains(p.Path) && p.References.Contains(project.Path)).Select(p => p.Path).ToList();
            if (tests.Count == 0)
            {
                skipped.AddRange(group.Files);
                log($"No test project unambiguously measures {label}; skipping. Use a test project whose selected framework references this context.");
                continue;
            }

            var groupFiles = group.Files.ToHashSet(StringComparer.Ordinal);
            var members = group.Types.SelectMany(t => t.Members).Where(m => groupFiles.Contains(m.Span.File)).ToList();
            var previous = workspace.Metrics.Mutation;
            var candidates = MutationMapper.Candidates(previous, members).ToList();
            var chosen = all ? candidates : MutationMapper.Changed(previous, members);
            if (!all && chosen.Count == 0) { log($"{label}: no changed members to mutate."); continue; }

            var testedFiles = all ? groupFiles : chosen.Select(m => m.Span.File).ToHashSet();
            var wholeFiles = testedFiles.Where(f => all || candidates.Where(m => m.Span.File == f).All(chosen.Contains)).ToHashSet();
            var testedIds = chosen.Select(m => m.Id).Concat(group.Types
                .Where(t => t.Files.All(wholeFiles.Contains)).Select(MutationMapper.InitializerId)).ToList();
            var mutants = await RunStrykerAsync(project, group.Context, tests, Patterns(project, chosen, testedFiles, wholeFiles), log, cancellation);
            var snapshot = MutationMapper.Merge(previous, model, mutants, testedIds, testedFiles, DateTimeOffset.UtcNow, group.Types);
            workspace.SaveMetrics(snapshot);
            projectCount++;
            memberCount += chosen.Count;
            var tested = mutants.Count(m => m.Status is MutantStatus.Killed or MutantStatus.Timeout or MutantStatus.Survived or MutantStatus.NoCoverage);
            mutantCount += tested;
            log($"{label}: {chosen.Count} members, {tested} mutants tested.");
        }
        return new MutationResult(projectCount, memberCount, mutantCount, skipped);
    }

    /// <summary>Whole files when every member is chosen, otherwise Stryker character spans per member.</summary>
    private IReadOnlyList<string> Patterns(BuildProject project, IReadOnlyList<MemberNode> chosen,
        IReadOnlySet<string> files, IReadOnlySet<string> wholeFiles)
    {
        var patterns = new List<string>();
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            var glob = "**/" + Path.GetRelativePath(project.Directory, workspace.Paths.Absolute(file)).Replace('\\', '/');
            if (wholeFiles.Contains(file)) patterns.Add(glob);
            else patterns.AddRange(chosen.Where(m => m.Span.File == file).Select(m => $"{glob}{{{m.Span.Start}..{m.Span.End}}}"));
        }
        return patterns;
    }

    private async Task<IReadOnlyList<Mutant>> RunStrykerAsync(
        BuildProject project, CompilerContext? context, IReadOnlyList<string> tests, IReadOnlyList<string> patterns, Action<string> log, CancellationToken cancellation)
    {
        var output = Path.Combine(workspace.Paths.RunDirectory, "stryker", $"{DateTime.UtcNow:yyyyMMddTHHmmss}-{project.Name}-{Guid.NewGuid():N}");
        var args = new List<string> { "stryker", "--project", Path.GetFileName(project.Path) };
        if ((context?.Configuration ?? workspace.ScanSettings.Configuration) is { } configuration) args.AddRange(["--configuration", configuration]);
        if (context?.Framework is { } framework) args.AddRange(["--target-framework", framework]);
        foreach (var test in tests) args.AddRange(["--test-project", test]);
        args.AddRange(["--reporter", "json", "--reporter", "progress", "--output", output]);
        foreach (var pattern in patterns) args.AddRange(["--mutate", pattern]);

        log($"dotnet stryker on {project.Name} ({patterns.Count} pattern(s))");
        var code = await processes.RunAsync("dotnet", args, project.Directory, log, cancellation);
        var report = Path.Combine(output, "reports", "mutation-report.json");
        if (!File.Exists(report))
            throw new MilligramException(
                $"Stryker exited {code} without a report for {project.Name}. Check the build and test output above, and {output}. " +
                "Run `milligram doctor` to check prerequisites. If Stryker is missing, use `dotnet tool restore` for a local manifest " +
                "or `dotnet tool install -g dotnet-stryker`.");
        return reports.Read(report, workspace.Paths.Root, project.Directory);
    }

    /// <summary>Accepts absolute paths or paths relative to the project root.</summary>
    private string Normalize(string file) =>
        Path.IsPathRooted(file) ? workspace.Paths.Relative(file) : file.Replace('\\', '/');

    private sealed record Target(BuildProject? Project, CompilerContext? Context, IReadOnlyList<string> Files, IReadOnlyList<TypeNode> Types);

    private IEnumerable<Target> Targets(CodeModel model, IReadOnlyList<string> files, IReadOnlyList<BuildProject> projects)
    {
        var evaluatedFiles = model.Types.Where(type => type.Context is not null).SelectMany(type => type.Files).ToHashSet(StringComparer.Ordinal);
        foreach (var group in model.Types.Where(type => type.Context is not null).GroupBy(type => type.Context!))
        {
            var owned = group.SelectMany(type => type.Files).Intersect(files, StringComparer.Ordinal).ToList();
            if (owned.Count == 0) continue;
            var project = projects.FirstOrDefault(project => workspace.Paths.Relative(project.Path) == group.Key.Project);
            yield return new Target(project, group.Key, owned, group.ToList());
        }
        foreach (var group in files.Where(file => !evaluatedFiles.Contains(file)).GroupBy(file => OwnerOf(file, projects)))
            yield return new Target(evaluatedFiles.Count > 0 ? null : group.Key, null, group.ToList(), model.Types.Where(type => type.Context is null).ToList());
    }

    private BuildProject? OwnerOf(string file, IReadOnlyList<BuildProject> projects)
    {
        var absolute = workspace.Paths.Absolute(file);
        return projects
            .Where(p => !p.IsTest && absolute.StartsWith(p.Directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .MaxBy(p => p.Directory.Length);
    }
}
