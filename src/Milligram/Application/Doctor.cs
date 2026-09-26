namespace Milligram.Application;

public enum CheckStatus { Ok, Failed, Skipped }

/// <summary>One prerequisite: what was found and, when something is missing, the commands that fix it.</summary>
public sealed record Check(string Name, CheckStatus Status, string Detail, IReadOnlyList<string> Fixes)
{
    public static Check Ok(string name, string detail) => new(name, CheckStatus.Ok, detail, []);
    public static Check Failed(string name, string detail, params string[] fixes) => new(name, CheckStatus.Failed, detail, fixes);
    public static Check Skipped(string name, string detail, params string[] fixes) => new(name, CheckStatus.Skipped, detail, fixes);

    public IEnumerable<string> Describe()
    {
        var mark = Status switch { CheckStatus.Ok => "✓", CheckStatus.Failed => "✗", _ => "–" };
        yield return $"{mark} {Name}: {Detail}";
        foreach (var fix in Fixes) yield return $"    Fix: {fix}";
    }
}

/// <summary>
/// Checks what Milligram needs, without running any tests: an SDK that works here, restored projects, test
/// projects with a coverage collector, Stryker.NET, and the companion agent. Anything missing comes with its fix.
/// </summary>
public sealed class Doctor(Workspace workspace, CrapService crap, IProjectLocator locator, IProcessRunner processes, ICompanion companion)
{
    private const int Listed = 3;

    public async Task<IReadOnlyList<Check>> RunAsync(CancellationToken cancellation)
    {
        var projects = locator.Find(workspace.Paths.Root);
        var tests = crap.TestProjects();
        return
        [
            await SdkAsync(cancellation),
            Restored(projects),
            Tests(tests),
            Coverage(projects, tests),
            await StrykerAsync(cancellation),
            Agent(),
        ];
    }

    /// <summary>`dotnet --version` in the project root honours its global.json, as `dotnet test` will.</summary>
    private async Task<Check> SdkAsync(CancellationToken cancellation)
    {
        var output = new List<string>();
        var code = await processes.RunAsync("dotnet", ["--version"], workspace.Paths.Root, output.Add, cancellation);
        if (code == 0 && output.LastOrDefault(l => Version.TryParse(l.Split('-')[0], out _)) is { } version)
            return Check.Ok(".NET SDK", version);
        var requested = output.FirstOrDefault(l => l.StartsWith("Requested SDK version:", StringComparison.Ordinal));
        var advice = output.FirstOrDefault(l => l.StartsWith("Install the ", StringComparison.Ordinal));
        return requested is not null
            ? Check.Failed(".NET SDK", $"global.json asks for an SDK that is not installed ({requested})", advice ?? "install that SDK, or update global.json")
            : Check.Failed(".NET SDK", code == 127 ? "dotnet is not on PATH" : $"`dotnet --version` failed (exit code {code})",
                "install the .NET SDK from https://dotnet.microsoft.com/download");
    }

    private Check Restored(IReadOnlyList<BuildProject> projects)
    {
        if (projects.Count == 0) return Check.Skipped("Restore", "no .csproj files under the project root");
        var missing = projects.Where(p => !p.IsRestored).ToList();
        return missing.Count == 0
            ? Check.Ok("Restore", $"{Count(projects.Count, "project")} restored")
            : Check.Failed("Restore",
                $"{missing.Count} of {Count(projects.Count, "project")} not restored ({Names(missing.Select(p => p.Path))}), so types from their packages don't resolve and edges to libraries are missing",
                "dotnet restore (on your solution), or build it once");
    }

    private Check Tests(IReadOnlyList<string> tests) =>
        tests.Count == 0
            ? Check.Failed("Tests", "no test projects found, so `milligram crap` and `milligram mutate` have nothing to run",
                "list them in milligram.json: \"tests\": { \"projects\": [\"tests/App.Tests/App.Tests.csproj\"] }")
            : Check.Ok("Tests", Names(tests));

    private Check Coverage(IReadOnlyList<BuildProject> projects, IReadOnlyList<string> tests)
    {
        if (tests.Count == 0) return Check.Skipped("Coverage", "no test projects");
        var byPath = projects.ToDictionary(p => p.Path, StringComparer.Ordinal);
        var uses = tests.ToDictionary(t => t, t => byPath.TryGetValue(t, out var p) ? locator.UsesPackage(p, CrapService.CoverageCollector) : null);
        var lacking = uses.Where(u => u.Value == false).Select(u => u.Key).ToList();
        if (lacking.Count > 0)
            return Check.Failed("Coverage", $"{Names(lacking)} {(lacking.Count == 1 ? "lacks" : "lack")} {CrapService.CoverageCollector}, so `milligram crap` can't measure {(lacking.Count == 1 ? "its" : "their")} coverage",
                lacking.Select(t => $"dotnet add {workspace.Paths.Relative(t)} package {CrapService.CoverageCollector}").ToArray());
        var unknown = uses.Where(u => u.Value is null).Select(u => u.Key).ToList();
        return unknown.Count == 0
            ? Check.Ok("Coverage", $"{CrapService.CoverageCollector} in {(tests.Count == 1 ? "the test project" : $"all {tests.Count} test projects")}")
            : Check.Skipped("Coverage", $"can't tell whether {Names(unknown)} {(unknown.Count == 1 ? "has" : "have")} {CrapService.CoverageCollector} until {(unknown.Count == 1 ? "it is" : "they are")} restored", "dotnet restore");
    }

    /// <summary>Runs Stryker the way `milligram mutate` does, so a local tool manifest counts as well as a global install.</summary>
    private async Task<Check> StrykerAsync(CancellationToken cancellation)
    {
        var code = await processes.RunAsync("dotnet", ["stryker", "--help"], workspace.Paths.Root, _ => { }, cancellation);
        return code == 0
            ? Check.Ok("Stryker.NET", "`dotnet stryker` runs")
            : Check.Failed("Stryker.NET", "`dotnet stryker` does not run here, so `milligram mutate` can't score tests",
                "dotnet tool install -g dotnet-stryker", "or, if the repository pins it in a tool manifest: dotnet tool restore");
    }

    private Check Agent()
    {
        var settings = workspace.Policy.Agent;
        if (!settings.Enabled) return Check.Skipped("Agent", "turned off in milligram.json (agent.enabled)");
        return companion.IsAvailable(out var reason)
            ? Check.Ok("Agent", $"tmux and {settings.Command} found")
            : Check.Failed("Agent", reason,
                "install what the agent needs (see the README's Requirements for your platform)",
                $"or run your own: {AgentBriefing.RunYourOwn(workspace.Paths, settings.Command)}",
                "or turn the agent off: \"agent\": { \"enabled\": false }");
    }

    private string Names(IEnumerable<string> paths)
    {
        var relative = paths.Select(workspace.Paths.Relative).ToList();
        var shown = string.Join(", ", relative.Take(Listed));
        return relative.Count > Listed ? $"{shown} and {relative.Count - Listed} more" : shown;
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
