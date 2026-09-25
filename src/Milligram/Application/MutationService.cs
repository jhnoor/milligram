using Milligram.Domain.Metrics;
using Milligram.Domain.Model;

namespace Milligram.Application;

public sealed record MutationResult(int Projects, int Members, int Mutants, IReadOnlyList<string> Skipped);

/// <summary>
/// Runs Stryker.NET on source files. By default only members whose code changed since their last
/// mutation run are mutated (differential); <c>all</c> mutates every member of the files.
/// </summary>
public sealed class MutationService(Workspace workspace, IProjectLocator locator, IProcessRunner processes, IMutationReportReader reports)
{
    public async Task<MutationResult> RunAsync(IReadOnlyList<string> files, bool all, Action<string> log, CancellationToken cancellation)
    {
        var model = workspace.Generate();
        var projects = locator.Find(workspace.Paths.Root);
        var targets = files.Count > 0 ? files.Select(Normalize).ToList() : model.Types.SelectMany(t => t.Files).Distinct().ToList();
        var skipped = new List<string>();
        int projectCount = 0, memberCount = 0, mutantCount = 0;

        foreach (var group in targets.GroupBy(f => OwnerOf(f, projects)))
        {
            if (group.Key is null) { skipped.AddRange(group); log($"No project owns: {string.Join(", ", group)}"); continue; }
            var tests = projects.Where(p => p.IsTest && p.References.Contains(group.Key.Path)).ToList();
            if (tests.Count == 0) { skipped.AddRange(group); log($"No test project references {group.Key.Name}; skipping."); continue; }

            var groupFiles = group.ToHashSet();
            var members = model.Types.SelectMany(t => t.Members).Where(m => groupFiles.Contains(m.Span.File)).ToList();
            var chosen = all ? members.Where(m => m.Complexity is not null).ToList() : MutationMapper.Changed(workspace.Metrics.Mutation, members);
            if (chosen.Count == 0) { log($"{group.Key.Name}: no changed members to mutate."); continue; }

            var mutants = await RunStrykerAsync(group.Key, tests, Patterns(group.Key, chosen, members), log, cancellation);
            var snapshot = MutationMapper.Merge(workspace.Metrics.Mutation, model, mutants, chosen.Select(m => m.Id).ToList(), groupFiles, DateTimeOffset.UtcNow);
            workspace.SaveMetrics(snapshot);
            projectCount++;
            memberCount += chosen.Count;
            var tested = mutants.Count(m => m.Status is MutantStatus.Killed or MutantStatus.Timeout or MutantStatus.Survived or MutantStatus.NoCoverage);
            mutantCount += tested;
            log($"{group.Key.Name}: {chosen.Count} members, {tested} mutants tested.");
        }
        return new MutationResult(projectCount, memberCount, mutantCount, skipped);
    }

    /// <summary>Whole files when every member is chosen, otherwise Stryker character spans per member.</summary>
    private IReadOnlyList<string> Patterns(BuildProject project, IReadOnlyList<MemberNode> chosen, IReadOnlyList<MemberNode> all)
    {
        var patterns = new List<string>();
        foreach (var file in chosen.GroupBy(m => m.Span.File))
        {
            var glob = "**/" + Path.GetRelativePath(project.Directory, workspace.Paths.Absolute(file.Key)).Replace('\\', '/');
            var everyMember = all.Where(m => m.Span.File == file.Key && m.Complexity is not null).All(m => file.Contains(m));
            if (everyMember) patterns.Add(glob);
            else patterns.AddRange(file.Select(m => $"{glob}{{{m.Span.Start}..{m.Span.End}}}"));
        }
        return patterns;
    }

    private async Task<IReadOnlyList<Mutant>> RunStrykerAsync(
        BuildProject project, IReadOnlyList<BuildProject> tests, IReadOnlyList<string> patterns, Action<string> log, CancellationToken cancellation)
    {
        var output = Path.Combine(workspace.Paths.RunDirectory, "stryker", $"{DateTime.UtcNow:yyyyMMddTHHmmss}-{project.Name}");
        var args = new List<string> { "stryker" };
        foreach (var test in tests) args.AddRange(["--test-project", test.Path]);
        args.AddRange(["--reporter", "json", "--reporter", "progress", "--output", output]);
        foreach (var pattern in patterns) args.AddRange(["--mutate", pattern]);

        log($"dotnet stryker on {project.Name} ({patterns.Count} pattern(s))");
        var code = await processes.RunAsync("dotnet", args, project.Directory, log, cancellation);
        var report = Path.Combine(output, "reports", "mutation-report.json");
        if (!File.Exists(report))
            throw new MilligramException(
                $"Stryker exited {code} without a report. Install it with `dotnet tool install -g dotnet-stryker` (or a local tool manifest).");
        return reports.Read(report, workspace.Paths.Root, project.Directory);
    }

    /// <summary>Accepts absolute paths or paths relative to the project root.</summary>
    private string Normalize(string file) =>
        Path.IsPathRooted(file) ? workspace.Paths.Relative(file) : file.Replace('\\', '/');

    private BuildProject? OwnerOf(string file, IReadOnlyList<BuildProject> projects)
    {
        var absolute = workspace.Paths.Absolute(file);
        return projects
            .Where(p => !p.IsTest && absolute.StartsWith(p.Directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .MaxBy(p => p.Directory.Length);
    }
}
