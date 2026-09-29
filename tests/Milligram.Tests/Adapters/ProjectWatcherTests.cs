using Milligram.Adapters.Files;
using Milligram.Analysis.CSharp;
using Milligram.Application;

namespace Milligram.Tests.Adapters;

public class ProjectWatcherTests
{
    [Theory]
    [InlineData(".", "Old", "Renamed", "Renamed/Order.cs")]
    [InlineData("./src/", "src/Old", "src/Renamed", "src/Renamed/Order.cs")]
    [InlineData("src", "src", "held", null)]
    [InlineData("src", "held", "src", "src/Order.cs")]
    [InlineData("src/App", "src", "held", null)]
    [InlineData(".", "Old", "bin", null)]
    [InlineData(".", "bin", "Old", "Old/Order.cs")]
    public async Task MovingAWholeFolderUpdatesTheScannedTree(string source, string before, string after, string? expectedFile)
    {
        using var project = new TempProject(("milligram.json", $$"""{ "src": "{{source}}" }"""),
            (before + "/Order.cs", "namespace Shop; public class Order { public int Total() => 1; }"));
        var paths = new ProjectPaths(project.Root);
        var workspace = new Workspace(paths, new CSharpScanner());
        workspace.Load();
        workspace.Generate();
        var events = new FakeEvents();
        var jobs = new JobQueue(events);
        using var watcher = new ProjectWatcher(paths, workspace, Actions(workspace, events, jobs), events);

        Directory.Move(paths.Absolute(before), paths.Absolute(after));

        var status = await Jobs.Finished(jobs, "Scan (source changed)");
        Assert.Equal(JobState.Succeeded, status.State);
        if (expectedFile is null) Assert.Empty(workspace.Model.Types);
        else Assert.Equal(expectedFile, Assert.Single(Assert.Single(workspace.Model.Types).Spans).File);
    }

    [Theory]
    [InlineData("src/Order.cs", false, true)]
    [InlineData("src/Domain", true, true)]
    [InlineData("src", true, true)]
    [InlineData("src", false, false)]
    [InlineData("srcOther/Order.cs", false, false)]
    [InlineData("srcOther", true, false)]
    [InlineData("docs", true, false)]
    [InlineData("src/bin", true, false)]
    [InlineData("src/obj/Order.cs", false, false)]
    [InlineData("src/.git", true, false)]
    [InlineData("src/.milligram", true, false)]
    [InlineData("src/node_modules", true, false)]
    [InlineData("src/.vs", true, false)]
    [InlineData("src/.idea", true, false)]
    [InlineData("src/Generated", true, false)]
    [InlineData("src/Generated/Order.cs", false, false)]
    [InlineData("src/Domain/Ignore.cs", false, false)]
    [InlineData("src/Domain/Ignore.cs", true, true)]
    public void OnlyChangesInsideTheSourceAndOutsideExcludedTreesNeedAScan(string relative, bool directory, bool expected)
    {
        using var project = new TempProject(("milligram.json", """
            { "src": "./src/", "exclude": ["src/Generated/**", "**/Ignore.cs"] }
            """));
        var workspace = new Workspace(new ProjectPaths(project.Root), new CSharpScanner());
        workspace.Load();
        var events = new FakeEvents();
        using var watcher = new ProjectWatcher(workspace.Paths, workspace, Actions(workspace, events, new JobQueue(events)), events);
        Assert.Equal(expected, watcher.IsScanned(relative, directory));
    }

    [Fact]
    public async Task RecoveringLostEventsReloadsThePolicyAndRescansSource()
    {
        using var project = new TempProject(("milligram.json", """{ "src": "src" }"""),
            ("src/Order.cs", "namespace Shop; public class Order { }"));
        var workspace = new Workspace(new ProjectPaths(project.Root), new CSharpScanner());
        workspace.Load();
        workspace.Generate();
        project.Write("milligram.json", """{ "src": "src", "title": "Recovered" }""");
        project.Write("src/Order.cs", "namespace Shop; public class Order { } public class Added { }");
        var events = new FakeEvents();
        var jobs = new JobQueue(events);
        using var watcher = new ProjectWatcher(workspace.Paths, workspace, Actions(workspace, events, jobs), events);

        watcher.RefreshAll();

        Assert.Equal(JobState.Succeeded, (await Jobs.Finished(jobs, "Scan (source changed)")).State);
        Assert.Equal("Recovered", workspace.Model.Title);
        Assert.Equal(2, workspace.Model.Types.Count);
        Assert.Contains("policy", events.Types);
        Assert.Contains("model", events.Types);
        Assert.Contains("metrics", events.Types);
    }

    private static ViewerActions Actions(Workspace workspace, FakeEvents events, JobQueue jobs)
    {
        var projects = new FakeProjectLocator();
        var processes = new FakeProcessRunner();
        return new ViewerActions(workspace, new PolicyEditor(workspace),
            new CrapService(workspace, projects, processes, new FakeCoverageReader(new(new Dictionary<string, IReadOnlyDictionary<int, int>>()))),
            new MutationService(workspace, projects, processes, new FakeMutationReader()), jobs, new FakeCompanion(), events);
    }
}
