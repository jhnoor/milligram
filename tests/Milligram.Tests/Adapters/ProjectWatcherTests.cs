using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Milligram.Adapters.Files;
using Milligram.Analysis.CSharp;
using Milligram.Application;

namespace Milligram.Tests.Adapters;

public class ProjectWatcherTests
{
    [Theory]
    [InlineData("App.csproj", "src/Order.cs")]
    [InlineData("src/App/App.csproj", "src/App/Order.cs")]
    public async Task ProjectEditsRefreshDependenciesWithoutSourceEdits(string projectFile, string sourceFile)
    {
        using var project = new TempProject(("milligram.json", """{ "src": "src", "foreign": ["System.Net.Http"] }"""),
            (projectFile, "<Project><PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>"),
            (sourceFile, "namespace Shop; public class Order { public HttpClient Client { get; } = new(); }"));
        var workspace = new Workspace(new ProjectPaths(project.Root), new CSharpScanner());
        workspace.Load();
        workspace.Generate();
        Assert.Contains(workspace.Model.Edges, edge => edge.To == "x:System.Net.Http");
        var events = new FakeEvents();
        var jobs = new JobQueue(events);
        using var watcher = new ProjectWatcher(workspace.Paths, workspace, Actions(workspace, events, jobs), events);

        project.Write(projectFile, "<Project><PropertyGroup><ImplicitUsings>disable</ImplicitUsings></PropertyGroup></Project>");

        Assert.Equal(JobState.Succeeded, (await Jobs.Finished(jobs, "Scan (source changed)")).State);
        Assert.Empty(workspace.Model.Edges);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("replace")]
    [InlineData("delete")]
    [InlineData("move-obj")]
    public async Task RestoredAssetsRefreshDependenciesWithoutSourceEdits(string change)
    {
        using var project = new TempProject(("milligram.json", """{ "src": "src", "foreign": ["Vendor"], "exclude": ["**/obj/**"] }"""),
            ("App.csproj", "<Project />"), ("src/Order.cs", "namespace Shop; public class Order : Vendor.Entity { }"));
        var assembly = project.Write("packages/vendor/1.0/Vendor.dll", "");
        var emitted = CSharpCompilation.Create("Vendor", [CSharpSyntaxTree.ParseText("namespace Vendor; public class Entity { }")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).Emit(assembly);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var packageRoot = Path.Combine(project.Root, "packages").Replace('\\', '/');
        var json = $$"""
            { "packageFolders": { "{{packageRoot}}": {} }, "libraries": { "Vendor/1.0": { "path": "vendor/1.0" } },
              "targets": { "net10.0": { "Vendor/1.0": { "type": "package", "compile": { "Vendor.dll": {} } } } } }
            """;
        var remove = change is "delete" or "move-obj";
        if (change != "create") project.Write("obj/project.assets.json", remove ? json : "{}");
        var workspace = new Workspace(new ProjectPaths(project.Root), new CSharpScanner());
        workspace.Load();
        workspace.Generate();
        Assert.Equal(remove, workspace.Model.Edges.Any(edge => edge.To == "x:Vendor"));
        var events = new FakeEvents();
        var jobs = new JobQueue(events);
        using var watcher = new ProjectWatcher(workspace.Paths, workspace, Actions(workspace, events, jobs), events);

        if (change == "delete") File.Delete(workspace.Paths.Absolute("obj/project.assets.json"));
        else if (change == "move-obj") Directory.Move(workspace.Paths.Absolute("obj"), workspace.Paths.Absolute("held"));
        else if (change == "replace") File.Move(project.Write("obj/assets.tmp", json), workspace.Paths.Absolute("obj/project.assets.json"), overwrite: true);
        else project.Write("obj/project.assets.json", json);

        Assert.Equal(JobState.Succeeded, (await Jobs.Finished(jobs, "Scan (source changed)")).State);
        Assert.Equal(!remove, workspace.Model.Edges.Any(edge => edge.To == "x:Vendor"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedUsingsRefreshBindingsWhenTheyAppearOrDisappear(bool remove)
    {
        using var project = new TempProject(("milligram.json", """{ "src": "src", "foreign": ["System.Net.Http"] }"""),
            ("src/App/App.csproj", "<Project />"),
            ("src/App/Order.cs", "namespace Shop; public class Order { public HttpClient Client { get; } = new(); }"));
        const string usings = "src/App/obj/Debug/net10.0/App.GlobalUsings.g.cs";
        if (remove) project.Write(usings, "global using System.Net.Http;");
        var workspace = new Workspace(new ProjectPaths(project.Root), new CSharpScanner());
        workspace.Load();
        workspace.Generate();
        Assert.Equal(remove, workspace.Model.Edges.Any(edge => edge.To == "x:System.Net.Http"));
        var events = new FakeEvents();
        var jobs = new JobQueue(events);
        using var watcher = new ProjectWatcher(workspace.Paths, workspace, Actions(workspace, events, jobs), events);

        if (remove) File.Delete(workspace.Paths.Absolute(usings));
        else project.Write(usings, "global using System.Net.Http;");

        Assert.Equal(JobState.Succeeded, (await Jobs.Finished(jobs, "Scan (source changed)")).State);
        Assert.Equal(!remove, workspace.Model.Edges.Any(edge => edge.To == "x:System.Net.Http"));
    }

    [Theory]
    [InlineData("App.csproj", true)]
    [InlineData("src/App/App.csproj", true)]
    [InlineData("obj/project.assets.json", true)]
    [InlineData("src/App/obj/project.assets.json", true)]
    [InlineData("obj/Debug/net10.0/App.GlobalUsings.g.cs", true)]
    [InlineData("src/App/obj/Debug/App.GlobalUsings.g.cs", true)]
    [InlineData("tests/Tests.csproj", false)]
    [InlineData("tests/obj/project.assets.json", false)]
    [InlineData("src/Generated/App.csproj", false)]
    [InlineData("src/Generated/obj/project.assets.json", false)]
    [InlineData("src/bin/App.csproj", false)]
    [InlineData("src/obj/App.csproj", false)]
    [InlineData("src/project.assets.json", false)]
    [InlineData("src/App/obj/Debug/project.assets.json", false)]
    [InlineData("src/App/obj/Debug/App.AssemblyInfo.cs", false)]
    [InlineData("src/App/obj/App.GlobalUsings.g.cs.tmp", false)]
    public void ProjectInputsBelongToScannedProjectsOrTheirAncestors(string relative, bool expected)
    {
        using var project = new TempProject(("milligram.json", """{ "src": "./src/", "exclude": ["src/Generated/**", "**/obj/**"] }"""));
        var workspace = new Workspace(new ProjectPaths(project.Root), new CSharpScanner());
        workspace.Load();
        var events = new FakeEvents();
        using var watcher = new ProjectWatcher(workspace.Paths, workspace, Actions(workspace, events, new JobQueue(events)), events);
        Assert.Equal(expected, watcher.IsProjectInput(relative));
    }

    [Fact]
    public async Task BuildNoiseAndSourcesOutsideThePolicyDoNotQueueScans()
    {
        using var project = new TempProject(("milligram.json", """{ "src": "src", "exclude": ["src/Generated/**"] }"""),
            ("src/App/obj/Generated.cs", ""), ("src/Generated/Skipped.cs", ""), ("tests/Skipped.cs", ""));
        var workspace = new Workspace(new ProjectPaths(project.Root), new CSharpScanner());
        workspace.Load();
        var events = new FakeEvents();
        var jobs = new JobQueue(events);
        using var watcher = new ProjectWatcher(workspace.Paths, workspace, Actions(workspace, events, jobs), events);

        project.Write("src/App/obj/Generated.cs", "class Generated {}");
        project.Write("src/Generated/Skipped.cs", "class Skipped {}");
        project.Write("tests/Skipped.cs", "class Test {}");
        project.Write("src/App/Order.cs.tmp", "class Draft {}");
        await Task.Delay(1200);

        Assert.Equal(JobState.Idle, jobs.Status.State);
    }

    [Fact]
    public async Task PolicyEditsReloadAndRegenerateTheLiveModel()
    {
        using var project = new TempProject(("milligram.json", """{ "src": "src" }"""), ("src/Order.cs", "class Order {}"));
        var workspace = new Workspace(new ProjectPaths(project.Root), new CSharpScanner());
        workspace.Load();
        var events = new FakeEvents();
        var jobs = new JobQueue(events);
        using var watcher = new ProjectWatcher(workspace.Paths, workspace, Actions(workspace, events, jobs), events);

        project.Write("milligram.json", """{ "src": "src", "title": "Edited" }""");

        Assert.Equal(JobState.Succeeded, (await Jobs.Finished(jobs, "Scan (milligram.json changed)")).State);
        Assert.Equal("Edited", workspace.Model.Title);
        Assert.Contains("policy", events.Types);
    }

    [Fact]
    public void PollingIncludesProjectInputsWithoutWalkingUnrelatedBuildOutput()
    {
        using var project = new TempProject(("milligram.json", """{ "src": "src", "exclude": ["src/Generated/**", "**/obj/**"] }"""),
            ("App.csproj", "<Project />"), ("src/App/App.csproj", "<Project />"),
            ("src/App/Order.cs", "class Order {}"), ("obj/project.assets.json", "{}"),
            ("src/App/obj/project.assets.json", "{}"), ("src/App/obj/Debug/App.GlobalUsings.g.cs", "global using System;"),
            ("src/App/obj/Debug/App.AssemblyInfo.cs", ""), ("src/App/obj/Debug/project.assets.json", "{}"),
            ("tests/Tests.csproj", "<Project />"), ("tests/obj/project.assets.json", "{}"),
            ("src/Generated/Skip.csproj", "<Project />"), ("src/Generated/obj/project.assets.json", "{}"));
        var workspace = new Workspace(new ProjectPaths(project.Root), new CSharpScanner());
        workspace.Load();
        var events = new FakeEvents();
        using var watcher = new ProjectWatcher(workspace.Paths, workspace, Actions(workspace, events, new JobQueue(events)), events);

        var inputs = watcher.Watched().Select(file => workspace.Paths.Relative(file.Key)).Order(StringComparer.Ordinal);

        Assert.Equal(["App.csproj", "milligram.json", "obj/project.assets.json", "src/App/App.csproj", "src/App/Order.cs",
            "src/App/obj/Debug/App.GlobalUsings.g.cs", "src/App/obj/project.assets.json"], inputs);
    }

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

        foreach (var name in new[] { "Scan (source changed)", "Scan (milligram.json changed)" })
        {
            var status = Assert.IsType<JobStatus>(await events.WaitFor("job",
                payload => payload is JobStatus status && status.Name == name && status.State is JobState.Succeeded or JobState.Failed));
            Assert.Equal(JobState.Succeeded, status.State);
        }
        await events.WaitFor("policy");
        await events.WaitFor("metrics");
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
