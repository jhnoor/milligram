using Milligram.Analysis.CSharp;
using Milligram.Application;
using Milligram.Domain.Metrics;

namespace Milligram.Tests.Application;

/// <summary>A one-project app with a test project, scanned for real; processes and reports are faked.</summary>
internal sealed class ServiceFixture : IDisposable
{
    public const string Source = """
        namespace App;
        public class A
        {
            public int One() => 1;
            public int Two(bool b) => b ? 2 : 0;
        }
        """;

    public ServiceFixture(string policy = """{ "prefix": "App", "src": "src" }""")
    {
        Project = new TempProject(("milligram.json", policy), ("src/App/App.csproj", "<Project />"), ("src/App/A.cs", Source));
        Workspace = new Workspace(new ProjectPaths(Project.Root), new CSharpScanner());
        Workspace.Load();
        AppProject = new BuildProject(Path.Combine(Project.Root, "src/App/App.csproj"), "App", false, []);
        TestProject = new BuildProject(Path.Combine(Project.Root, "tests/App.Tests/App.Tests.csproj"), "App.Tests", true, [AppProject.Path]);
    }

    public TempProject Project { get; }
    public Workspace Workspace { get; }
    public BuildProject AppProject { get; }
    public BuildProject TestProject { get; }

    public void Dispose() => Project.Dispose();
}

public class CrapServiceTests
{
    private static readonly LineHits Hits = new(new Dictionary<string, IReadOnlyDictionary<int, int>>
    {
        ["src/App/A.cs"] = new Dictionary<int, int> { [4] = 3, [5] = 0 },
    });

    /// <summary>Plays `dotnet test --collect`: drops a Cobertura file in the results directory.</summary>
    private static int WriteCoverage(string command, IReadOnlyList<string> args, string directory)
    {
        var results = FakeProcessRunner.After(args, "--results-directory");
        Directory.CreateDirectory(Path.Combine(results, "guid"));
        File.WriteAllText(Path.Combine(results, "guid", "coverage.cobertura.xml"), "<coverage />");
        return 0;
    }

    [Fact]
    public async Task RunsTheDetectedTestProjectsWithCoverageAndScoresEveryMember()
    {
        using var fixture = new ServiceFixture();
        var processes = new FakeProcessRunner(WriteCoverage);
        var reader = new FakeCoverageReader(Hits);
        var service = new CrapService(fixture.Workspace, new FakeProjectLocator(fixture.AppProject, fixture.TestProject), processes, reader);

        var result = await service.RunAsync(null, _ => { }, CancellationToken.None);

        var (command, args, _) = Assert.Single(processes.Calls);
        Assert.Equal("dotnet", command);
        Assert.Equal(["test", fixture.TestProject.Path, "--collect", "XPlat Code Coverage"], args.Take(4));
        Assert.Single(reader.Reports);
        Assert.Equal(2, result.Members);
        Assert.Equal(0, result.TestExitCode);

        var snapshot = JsonFile.Read<CrapSnapshot>(fixture.Workspace.Paths.CrapFile)!;
        Assert.Equal(1.0, snapshot.Members["App.A.One()"].Coverage);
        var two = snapshot.Members["App.A.Two(bool)"];
        Assert.Equal(2, two.Complexity);
        Assert.Equal(0, two.Coverage);
        Assert.Equal(6, two.Crap);
        Assert.Equal(2, fixture.Workspace.Metrics.Crap.Members.Count);
    }

    [Fact]
    public async Task ConfiguredTestProjectsAndFilterWin()
    {
        using var fixture = new ServiceFixture("""{ "prefix": "App", "src": "src", "tests": { "projects": ["tests/Other/Other.csproj"], "filter": "Category=Fast" } }""");
        var processes = new FakeProcessRunner(WriteCoverage);
        var service = new CrapService(fixture.Workspace, new FakeProjectLocator(fixture.TestProject), processes, new FakeCoverageReader(Hits));

        await service.RunAsync(null, _ => { }, CancellationToken.None);

        var args = Assert.Single(processes.Calls).Args;
        Assert.Equal(Path.Combine(fixture.Project.Root, "tests/Other/Other.csproj"), args[1]);
        Assert.Equal("Category=Fast", FakeProcessRunner.After(args, "--filter"));
    }

    [Fact]
    public async Task GivenReportsSkipTheTestRun()
    {
        using var fixture = new ServiceFixture();
        var processes = new FakeProcessRunner();
        var reader = new FakeCoverageReader(Hits);
        var service = new CrapService(fixture.Workspace, new FakeProjectLocator(), processes, reader);

        var result = await service.RunAsync(["/some/coverage.xml"], _ => { }, CancellationToken.None);

        Assert.Empty(processes.Calls);
        Assert.Equal(["/some/coverage.xml"], reader.Reports);
        Assert.Equal(2, result.Members);
    }

    [Fact]
    public async Task FailingTestsStillScoreButReportTheExitCode()
    {
        using var fixture = new ServiceFixture();
        var processes = new FakeProcessRunner((c, a, d) => { WriteCoverage(c, a, d); return 1; });
        var service = new CrapService(fixture.Workspace, new FakeProjectLocator(fixture.TestProject), processes, new FakeCoverageReader(Hits));

        var result = await service.RunAsync(null, _ => { }, CancellationToken.None);

        Assert.Equal(1, result.TestExitCode);
        Assert.Equal(2, result.Members);
    }

    [Fact]
    public async Task NoTestProjectsIsAnError()
    {
        using var fixture = new ServiceFixture();
        var service = new CrapService(fixture.Workspace, new FakeProjectLocator(fixture.AppProject), new FakeProcessRunner(), new FakeCoverageReader(Hits));
        var error = await Assert.ThrowsAsync<MilligramException>(() => service.RunAsync(null, _ => { }, CancellationToken.None));
        Assert.Contains("No test projects", error.Message);
    }

    [Fact]
    public async Task NoCoverageReportIsAnError()
    {
        using var fixture = new ServiceFixture();
        var service = new CrapService(fixture.Workspace, new FakeProjectLocator(fixture.TestProject), new FakeProcessRunner(), new FakeCoverageReader(Hits));
        var error = await Assert.ThrowsAsync<MilligramException>(() => service.RunAsync(null, _ => { }, CancellationToken.None));
        Assert.Contains("coverlet", error.Message);
    }

    [Fact]
    public void TestProjectsPrefersThePolicy()
    {
        using var fixture = new ServiceFixture("""{ "prefix": "App", "tests": { "projects": ["t/T.csproj"] } }""");
        var service = new CrapService(fixture.Workspace, new FakeProjectLocator(fixture.TestProject), new FakeProcessRunner(), new FakeCoverageReader(Hits));
        Assert.Equal([Path.Combine(fixture.Project.Root, "t/T.csproj")], service.TestProjects());
    }

    [Fact]
    public async Task StopsBeforeRunningTestsWhenNoTestProjectCanMeasureCoverage()
    {
        using var fixture = new ServiceFixture();
        var other = new BuildProject(Path.Combine(fixture.Project.Root, "tests/Other.Tests/Other.Tests.csproj"), "Other.Tests", true, [fixture.AppProject.Path]);
        var processes = new FakeProcessRunner(WriteCoverage);
        var locator = new FakeProjectLocator(fixture.AppProject, fixture.TestProject, other) { Uses = (_, _) => false };
        var service = new CrapService(fixture.Workspace, locator, processes, new FakeCoverageReader(Hits));

        var refused = await Assert.ThrowsAsync<MilligramException>(() => service.RunAsync(null, _ => { }, CancellationToken.None));

        Assert.Empty(processes.Calls);
        Assert.False(File.Exists(fixture.Workspace.Paths.ModelFile));
        Assert.Equal(
            "No test project can measure coverage: they lack coverlet.collector. Fix: dotnet add tests/App.Tests/App.Tests.csproj package coverlet.collector; " +
            "dotnet add tests/Other.Tests/Other.Tests.csproj package coverlet.collector",
            refused.Message);
    }

    [Fact]
    public async Task WarnsAboutTestProjectsWithoutACollectorAndRunsTheRest()
    {
        using var fixture = new ServiceFixture();
        var other = new BuildProject(Path.Combine(fixture.Project.Root, "tests/Other.Tests/Other.Tests.csproj"), "Other.Tests", true, [fixture.AppProject.Path]);
        var fresh = new BuildProject(Path.Combine(fixture.Project.Root, "tests/New.Tests/New.Tests.csproj"), "New.Tests", true, [fixture.AppProject.Path]);
        var processes = new FakeProcessRunner(WriteCoverage);
        var locator = new FakeProjectLocator(fixture.AppProject, fixture.TestProject, other, fresh)
        {
            Uses = (project, package) => package != CrapService.CoverageCollector || project == fixture.TestProject ? true : project == fresh ? null : false,
        };
        var log = new List<string>();

        await new CrapService(fixture.Workspace, locator, processes, new FakeCoverageReader(Hits)).RunAsync(null, log.Add, CancellationToken.None);

        Assert.Equal(3, processes.Calls.Count);
        Assert.Contains(
            "tests/Other.Tests/Other.Tests.csproj lacks coverlet.collector, so its tests add no coverage. Fix: dotnet add tests/Other.Tests/Other.Tests.csproj package coverlet.collector",
            log);
        Assert.DoesNotContain(log, line => line.StartsWith("tests/New.Tests", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadingACoverageFileNeedsNoCollector()
    {
        using var fixture = new ServiceFixture();
        var locator = new FakeProjectLocator(fixture.AppProject, fixture.TestProject) { Uses = (_, _) => false };
        var service = new CrapService(fixture.Workspace, locator, new FakeProcessRunner(), new FakeCoverageReader(Hits));

        var result = await service.RunAsync(["coverage.xml"], _ => { }, CancellationToken.None);

        Assert.Equal(2, result.Members);
    }
}

public class MutationServiceTests
{
    private static readonly Mutant[] Mutants =
    [
        new("src/App/A.cs", 4, 25, MutantStatus.Killed, "number"),
        new("src/App/A.cs", 5, 32, MutantStatus.Survived, "conditional"),
        new("src/App/A.cs", 5, 36, MutantStatus.NoCoverage, "number"),
        new("src/App/A.cs", 5, 40, MutantStatus.Ignored, "number"),
    ];

    /// <summary>Plays Stryker: writes a report where --output says.</summary>
    private static int WriteReport(string command, IReadOnlyList<string> args, string directory)
    {
        var reports = Path.Combine(FakeProcessRunner.After(args, "--output"), "reports");
        Directory.CreateDirectory(reports);
        File.WriteAllText(Path.Combine(reports, "mutation-report.json"), "{}");
        return 0;
    }

    private static (MutationService Service, FakeProcessRunner Processes) Service(ServiceFixture fixture, params BuildProject[] projects)
    {
        var processes = new FakeProcessRunner(WriteReport);
        var locator = new FakeProjectLocator(projects.Length > 0 ? projects : [fixture.AppProject, fixture.TestProject]);
        return (new MutationService(fixture.Workspace, locator, processes, new FakeMutationReader(Mutants)), processes);
    }

    [Fact]
    public async Task AFirstRunMutatesWholeFilesAndRecordsEachMember()
    {
        using var fixture = new ServiceFixture();
        var (service, processes) = Service(fixture);

        var result = await service.RunAsync(["src/App/A.cs"], all: false, _ => { }, CancellationToken.None);

        var (command, args, directory) = Assert.Single(processes.Calls);
        Assert.Equal("dotnet", command);
        Assert.Equal("stryker", args[0]);
        Assert.Equal(fixture.AppProject.Directory, directory);
        Assert.Equal(fixture.TestProject.Path, FakeProcessRunner.After(args, "--test-project"));
        Assert.Contains("json", FakeProcessRunner.AllAfter(args, "--reporter"));
        Assert.Equal(["**/A.cs"], FakeProcessRunner.AllAfter(args, "--mutate"));
        Assert.Equal((1, 2, 3), (result.Projects, result.Members, result.Mutants));

        var snapshot = JsonFile.Read<MutationSnapshot>(fixture.Workspace.Paths.MutationFile)!;
        Assert.Equal(1, snapshot.Members["App.A.One()"].Killed);
        Assert.Equal((1, 1), (snapshot.Members["App.A.Two(bool)"].Survived, snapshot.Members["App.A.Two(bool)"].Uncovered));
        Assert.True(snapshot.Tested("src/App/A.cs"));
    }

    [Fact]
    public async Task UnchangedMembersAreNotMutatedAgain()
    {
        using var fixture = new ServiceFixture();
        var (service, processes) = Service(fixture);
        await service.RunAsync(["src/App/A.cs"], all: false, _ => { }, CancellationToken.None);
        var log = new List<string>();

        var result = await service.RunAsync(["src/App/A.cs"], all: false, log.Add, CancellationToken.None);

        Assert.Single(processes.Calls);
        Assert.Equal(0, result.Members);
        Assert.Contains(log, l => l.Contains("no changed members"));
    }

    [Fact]
    public async Task AChangedMemberIsMutatedByItsCharacterSpan()
    {
        using var fixture = new ServiceFixture();
        var (service, processes) = Service(fixture);
        await service.RunAsync(["src/App/A.cs"], all: false, _ => { }, CancellationToken.None);
        fixture.Project.Write("src/App/A.cs", ServiceFixture.Source.Replace("b ? 2 : 0", "b ? 3 : 0"));

        await service.RunAsync(["src/App/A.cs"], all: false, _ => { }, CancellationToken.None);

        var two = fixture.Workspace.Model.Types.Single().Members.Single(m => m.Name == "Two").Span;
        Assert.Equal([$"**/A.cs{{{two.Start}..{two.End}}}"], FakeProcessRunner.AllAfter(processes.Calls[1].Args, "--mutate"));
    }

    [Fact]
    public async Task AllMutatesUnchangedFilesAnyway()
    {
        using var fixture = new ServiceFixture();
        var (service, processes) = Service(fixture);
        await service.RunAsync(["src/App/A.cs"], all: false, _ => { }, CancellationToken.None);

        var result = await service.RunAsync([Path.Combine(fixture.Project.Root, "src/App/A.cs")], all: true, _ => { }, CancellationToken.None);

        Assert.Equal(2, processes.Calls.Count);
        Assert.Equal(["**/A.cs"], FakeProcessRunner.AllAfter(processes.Calls[1].Args, "--mutate"));
        Assert.Equal(2, result.Members);
    }

    [Fact]
    public async Task NoFilesMeansEveryScannedFile()
    {
        using var fixture = new ServiceFixture();
        var (service, processes) = Service(fixture);
        await service.RunAsync([], all: false, _ => { }, CancellationToken.None);
        Assert.Single(processes.Calls);
    }

    [Fact]
    public async Task FilesWithoutAProjectOrTestsAreSkipped()
    {
        using var fixture = new ServiceFixture();
        var (orphan, orphanProcesses) = Service(fixture, fixture.TestProject);
        var untested = new BuildProject(fixture.AppProject.Path, "App", false, []);
        var (lonely, lonelyProcesses) = Service(fixture, untested);

        Assert.Equal(["src/App/A.cs"], (await orphan.RunAsync(["src/App/A.cs"], false, _ => { }, CancellationToken.None)).Skipped);
        Assert.Equal(["src/App/A.cs"], (await lonely.RunAsync(["src/App/A.cs"], false, _ => { }, CancellationToken.None)).Skipped);
        Assert.Empty(orphanProcesses.Calls);
        Assert.Empty(lonelyProcesses.Calls);
    }

    [Fact]
    public async Task AMissingReportExplainsHowToInstallStryker()
    {
        using var fixture = new ServiceFixture();
        var service = new MutationService(fixture.Workspace, new FakeProjectLocator(fixture.AppProject, fixture.TestProject),
            new FakeProcessRunner((_, _, _) => 1), new FakeMutationReader());

        var error = await Assert.ThrowsAsync<MilligramException>(() => service.RunAsync(["src/App/A.cs"], false, _ => { }, CancellationToken.None));

        Assert.Contains("dotnet-stryker", error.Message);
    }
}
