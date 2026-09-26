using Milligram.Application;
using Milligram.Domain.Metrics;

namespace Milligram.Tests.Application;

public class DoctorTests
{
    private static Doctor Doctor(ServiceFixture fixture, FakeProjectLocator locator, FakeProcessRunner processes, FakeCompanion companion, WatchLimit? limit = null) =>
        new(fixture.Workspace, new CrapService(fixture.Workspace, locator, processes, new FakeCoverageReader(new LineHits(new Dictionary<string, IReadOnlyDictionary<int, int>>()))),
            locator, processes, companion, new FakeWatchLimits(limit));

    private static string Report(IReadOnlyList<Check> checks) => string.Join("\n", checks.SelectMany(c => c.Describe()));

    [Fact]
    public async Task ReportsEverythingInPlace()
    {
        using var fixture = new ServiceFixture();
        var processes = new FakeProcessRunner { Output = { ["--version"] = ["10.0.401"] } };
        var locator = new FakeProjectLocator(fixture.AppProject with { IsRestored = true }, fixture.TestProject with { IsRestored = true });

        var checks = await Doctor(fixture, locator, processes, new FakeCompanion()).RunAsync(CancellationToken.None);

        Assert.Equal(
            """
            ✓ .NET SDK: 10.0.401
            ✓ Restore: 2 projects restored
            ✓ Tests: tests/App.Tests/App.Tests.csproj
            ✓ Coverage: coverlet.collector in the test project
            ✓ Stryker.NET: `dotnet stryker` runs
            ✓ Agent: tmux and copilot found
            """,
            Report(checks));
        Assert.Equal([("dotnet", "--version"), ("dotnet", "stryker --help")], processes.Calls.Select(c => (c.Command, string.Join(' ', c.Args))));
        Assert.All(processes.Calls, c => Assert.Equal(fixture.Project.Root, c.Directory));
    }

    [Fact]
    public async Task SaysHowToFixWhatIsMissing()
    {
        using var fixture = new ServiceFixture();
        var processes = new FakeProcessRunner((_, _, _) => 1)
        {
            Output =
            {
                ["--version"] =
                [
                    "A compatible .NET SDK was not found.", "Requested SDK version: 8.0.100", "global.json file: /repo/global.json",
                    "Install the [8.0.100] .NET SDK or update [/repo/global.json] to match an installed SDK.",
                ],
            },
        };
        var more = Enumerable.Range(1, 4).Select(i => new BuildProject(Path.Combine(fixture.Project.Root, $"src/P{i}/P{i}.csproj"), $"P{i}", false, []));
        var locator = new FakeProjectLocator([fixture.AppProject with { IsRestored = true }, fixture.TestProject, .. more]) { Uses = (_, _) => false };

        var checks = await Doctor(fixture, locator, processes, new FakeCompanion { Available = false }).RunAsync(CancellationToken.None);

        Assert.Equal(
            """
            ✗ .NET SDK: global.json asks for an SDK that is not installed (Requested SDK version: 8.0.100)
                Fix: Install the [8.0.100] .NET SDK or update [/repo/global.json] to match an installed SDK.
            ✗ Restore: 5 of 6 projects not restored (tests/App.Tests/App.Tests.csproj, src/P1/P1.csproj, src/P2/P2.csproj and 2 more), so types from their packages don't resolve and edges to libraries are missing
                Fix: dotnet restore (on your solution), or build it once
            ✓ Tests: tests/App.Tests/App.Tests.csproj
            ✗ Coverage: tests/App.Tests/App.Tests.csproj lacks coverlet.collector, so `milligram crap` can't measure its coverage
                Fix: dotnet add tests/App.Tests/App.Tests.csproj package coverlet.collector
            ✗ Stryker.NET: `dotnet stryker` does not run here, so `milligram mutate` can't score tests
                Fix: dotnet tool install -g dotnet-stryker
                Fix: or, if the repository pins it in a tool manifest: dotnet tool restore
            ✗ Agent: tmux is not installed.
                Fix: install what the agent needs (see the README's Requirements for your platform)
                Fix: or run your own: start copilot in the project folder and tell it to read .milligram/agent.md
                Fix: or turn the agent off: "agent": { "enabled": false }
            """,
            Report(checks));
    }

    [Fact]
    public async Task SaysWhenTheWatcherCantSeeEveryChangeAsItHappens()
    {
        using var fixture = new ServiceFixture();
        var processes = new FakeProcessRunner { Output = { ["--version"] = ["10.0.401"] } };
        var limit = new WatchLimit("the project is on a Windows drive", "keep it in the Linux file system");

        var checks = await Doctor(fixture, new FakeProjectLocator(), processes, new FakeCompanion(), limit).RunAsync(CancellationToken.None);
        var unlimited = await Doctor(fixture, new FakeProjectLocator(), processes, new FakeCompanion()).RunAsync(CancellationToken.None);

        Assert.Equal(["– Watching: the project is on a Windows drive", "    Fix: keep it in the Linux file system"], checks[^1].Describe());
        Assert.Equal(CheckStatus.Skipped, checks[^1].Status);
        Assert.DoesNotContain(unlimited, check => check.Name == "Watching");
    }

    [Theory]
    [InlineData(127, "dotnet is not on PATH")]
    [InlineData(1, "`dotnet --version` failed (exit code 1)")]
    public async Task ExplainsAnSdkThatDoesNotRun(int exitCode, string detail)
    {
        using var fixture = new ServiceFixture();
        var processes = new FakeProcessRunner((_, args, _) => args[0] == "--version" ? exitCode : 0);

        var sdk = (await Doctor(fixture, new FakeProjectLocator(), processes, new FakeCompanion()).RunAsync(CancellationToken.None))[0];

        Assert.Equal(
            [$"✗ .NET SDK: {detail}", "    Fix: install the .NET SDK from https://dotnet.microsoft.com/download"],
            sdk.Describe());
    }

    [Fact]
    public async Task APinnedSdkWithoutAdviceStillGetsAFix()
    {
        using var fixture = new ServiceFixture();
        var processes = new FakeProcessRunner((_, _, _) => 155) { Output = { ["--version"] = ["Requested SDK version: 9.0.100"] } };

        var sdk = (await Doctor(fixture, new FakeProjectLocator(), processes, new FakeCompanion()).RunAsync(CancellationToken.None))[0];

        Assert.Equal("    Fix: install that SDK, or update global.json", sdk.Describe().Last());
    }

    [Fact]
    public async Task SkipsWhatItCannotJudgeYet()
    {
        using var fixture = new ServiceFixture("""{ "prefix": "App", "agent": { "enabled": false } }""");
        var second = fixture.TestProject with { Path = Path.Combine(fixture.Project.Root, "tests/B.Tests/B.Tests.csproj") };
        var processes = new FakeProcessRunner { Output = { ["--version"] = ["10.0.100-rc.2"] } };
        var locator = new FakeProjectLocator(fixture.TestProject, second) { Uses = (p, _) => p == second ? null : true };

        var checks = await Doctor(fixture, locator, processes, new FakeCompanion()).RunAsync(CancellationToken.None);

        Assert.Equal("✓ .NET SDK: 10.0.100-rc.2", checks[0].Describe().Single());
        Assert.Equal(
            ["– Coverage: can't tell whether tests/B.Tests/B.Tests.csproj has coverlet.collector until it is restored", "    Fix: dotnet restore"],
            checks[3].Describe());
        Assert.Equal("– Agent: turned off in milligram.json (agent.enabled)", checks[5].Describe().Single());
    }

    [Fact]
    public async Task WithoutProjectsOrTestsThereIsNothingToCheck()
    {
        using var fixture = new ServiceFixture();
        var processes = new FakeProcessRunner { Output = { ["--version"] = ["10.0.401"] } };

        var checks = await Doctor(fixture, new FakeProjectLocator(), processes, new FakeCompanion()).RunAsync(CancellationToken.None);

        Assert.Equal("– Restore: no .csproj files under the project root", checks[1].Describe().Single());
        Assert.Equal(
            [
                "✗ Tests: no test projects found, so `milligram crap` and `milligram mutate` have nothing to run",
                """    Fix: list them in milligram.json: "tests": { "projects": ["tests/App.Tests/App.Tests.csproj"] }""",
            ],
            checks[2].Describe());
        Assert.Equal("– Coverage: no test projects", checks[3].Describe().Single());
    }

    [Fact]
    public async Task CountsSeveralTestProjects()
    {
        using var fixture = new ServiceFixture();
        var second = fixture.TestProject with { Path = Path.Combine(fixture.Project.Root, "tests/B.Tests/B.Tests.csproj") };
        var unknown = fixture.TestProject with { Path = Path.Combine(fixture.Project.Root, "tests/C.Tests/C.Tests.csproj") };
        var processes = new FakeProcessRunner { Output = { ["--version"] = ["10.0.401"] } };
        var one = new FakeProjectLocator(fixture.AppProject with { IsRestored = true }, fixture.TestProject, second, unknown)
        {
            Uses = (p, _) => p == unknown ? null : p != second,
        };

        var checks = await Doctor(fixture, one, processes, new FakeCompanion()).RunAsync(CancellationToken.None);

        Assert.Equal("✓ Tests: tests/App.Tests/App.Tests.csproj, tests/B.Tests/B.Tests.csproj, tests/C.Tests/C.Tests.csproj", checks[2].Describe().Single());
        Assert.Equal(
            [
                "✗ Coverage: tests/B.Tests/B.Tests.csproj lacks coverlet.collector, so `milligram crap` can't measure its coverage",
                "    Fix: dotnet add tests/B.Tests/B.Tests.csproj package coverlet.collector",
            ],
            checks[3].Describe());
        Assert.StartsWith("✗ Restore: 3 of 4 projects not restored", checks[1].Describe().First());

        var two = new FakeProjectLocator(fixture.TestProject, second, unknown) { Uses = (p, _) => p == fixture.TestProject };
        var lacking = (await Doctor(fixture, two, processes, new FakeCompanion()).RunAsync(CancellationToken.None))[3];
        Assert.StartsWith("✗ Coverage: tests/B.Tests/B.Tests.csproj, tests/C.Tests/C.Tests.csproj lack coverlet.collector, so `milligram crap` can't measure their coverage", lacking.Describe().First());

        var unknowns = new FakeProjectLocator(fixture.TestProject, second) { Uses = (_, _) => null };
        var cannotTell = (await Doctor(fixture, unknowns, processes, new FakeCompanion()).RunAsync(CancellationToken.None))[3];
        Assert.StartsWith("– Coverage: can't tell whether tests/App.Tests/App.Tests.csproj, tests/B.Tests/B.Tests.csproj have coverlet.collector until they are restored", cannotTell.Describe().First());

        var covered = new FakeProjectLocator(fixture.TestProject, second);
        var all = (await Doctor(fixture, covered, processes, new FakeCompanion()).RunAsync(CancellationToken.None))[3];
        Assert.Equal("✓ Coverage: coverlet.collector in all 2 test projects", all.Describe().Single());
    }

    [Fact]
    public async Task OneRestoredProjectIsCountedInTheSingular()
    {
        using var fixture = new ServiceFixture();
        var processes = new FakeProcessRunner { Output = { ["--version"] = ["10.0.401"] } };

        var checks = await Doctor(fixture, new FakeProjectLocator(fixture.AppProject with { IsRestored = true }), processes, new FakeCompanion()).RunAsync(CancellationToken.None);

        Assert.Equal("✓ Restore: 1 project restored", checks[1].Describe().Single());
    }
}
