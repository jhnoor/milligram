using Milligram.Adapters.Files;
using Milligram.Analysis.CSharp;
using Milligram.Application;
using Milligram.Domain.Model;
using Milligram.Domain.Policies;

namespace Milligram.Tests.Application;

public class StartupScanTests
{
    [Fact]
    public async Task AFirstRunPublishesAndSavesItsSingleScanAndLaterRegenerationReadsCurrentSource()
    {
        using var project = new TempProject(("Order.cs", "namespace Shop; public class Order { }"));
        var paths = new ProjectPaths(project.Root);
        using var inputs = new InitializationInputs(paths);
        var scanner = new CountingScanner();
        var initialization = new ProjectInitializer(paths, scanner, new FakeProjectLocator(), new NewFilePublisher()).Initialize(false)!;
        var workspace = new Workspace(paths, scanner);
        workspace.Load();
        var metrics = workspace.Metrics;
        var events = new FakeEvents();
        var jobs = new JobQueue(events);
        var actions = Actions(workspace, events, jobs);

        await actions.Start(initialization, inputs.IsCurrent);

        Assert.Equal(1, scanner.Calls);
        Assert.Same(initialization.Model, workspace.Model);
        Assert.Same(metrics, workspace.Metrics);
        Assert.Equal("Shop", JsonFile.Read<CodeModel>(paths.ModelFile)!.Prefix);
        Assert.Equal(JobState.Succeeded, jobs.Status.State);
        Assert.Equal("Scan", jobs.Status.Name);
        Assert.Contains("model", events.Types);
        Assert.Contains(jobs.Status.Log, line => line.Contains("Reusing the initialization scan", StringComparison.Ordinal));

        project.Write("Order.cs", "namespace Shop; public class Order { } public class Added { }");
        await actions.Regenerate();

        Assert.Equal(2, scanner.Calls);
        Assert.Equal(2, workspace.Model.Types.Count);
    }

    [Fact]
    public async Task ChangesMadeWhileInitializationIsScanningAreReadAgain()
    {
        using var project = new TempProject(("Order.cs", "namespace Shop; public class Order { }"));
        var paths = new ProjectPaths(project.Root);
        using var inputs = new InitializationInputs(paths);
        var scanner = new CountingScanner
        {
            AfterFirstScan = () => project.Write("Order.cs", "namespace Shop; public class Order { } public class Added { }"),
        };
        var initialization = new ProjectInitializer(paths, scanner, new FakeProjectLocator(), new NewFilePublisher()).Initialize(false)!;
        Assert.Single(initialization.Model!.Types);
        var workspace = new Workspace(paths, scanner);
        workspace.Load();
        var events = new FakeEvents();

        await Actions(workspace, events, new JobQueue(events)).Start(initialization, inputs.IsCurrent);

        Assert.Equal(2, scanner.Calls);
        Assert.Equal(2, workspace.Model.Types.Count);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("prefix")]
    [InlineData("src")]
    [InlineData("exclude")]
    [InlineData("foreign")]
    [InlineData("scanMode")]
    [InlineData("scanConfiguration")]
    [InlineData("scanProjects")]
    [InlineData("broken")]
    public void ChangedScanSettingsOrAnInvalidPolicyCannotReuseTheInitialModel(string change)
    {
        using var project = new TempProject(("Order.cs", "namespace Shop; public class Order { }"));
        var paths = new ProjectPaths(project.Root);
        var scanner = new CountingScanner();
        var initialization = new ProjectInitializer(paths, scanner, new FakeProjectLocator(), new NewFilePublisher()).Initialize(false)!;
        var workspace = new Workspace(paths, scanner);
        workspace.Load();
        var original = workspace.Policy;
        if (change == "broken") project.Write("milligram.json", "{");
        else workspace.EditPolicy(policy => change switch
        {
            "title" => policy with { Title = "Changed" },
            "prefix" => policy with { Prefix = "Changed" },
            "src" => policy with { Src = "different" },
            "exclude" => policy with { Exclude = ["**"] },
            "scanMode" => policy with { Scan = new ScanSettings { Mode = ScanMode.SourceOnly } },
            "scanConfiguration" => policy with { Scan = new ScanSettings { Configuration = "Release" } },
            "scanProjects" => policy with { Scan = new ScanSettings { Projects = ["App.csproj"] } },
            _ => policy with { Foreign = ["System"] },
        });
        workspace.ReloadPolicy();

        workspace.GenerateOrReuse(initialization, inputsUnchanged: true);

        Assert.Equal(2, scanner.Calls);
        Assert.NotSame(initialization.Model, workspace.Model);
        Assert.Equal(workspace.Policy.Prefix, workspace.Model.Prefix);
        if (change == "broken") Assert.Equal(original, workspace.Policy);
    }

    [Fact]
    public void DisplayOnlyPolicyChangesCanUseTheInitializationModel()
    {
        using var project = new TempProject(("Order.cs", "namespace Shop; public class Order { }"));
        var paths = new ProjectPaths(project.Root);
        var scanner = new CountingScanner();
        var initialization = new ProjectInitializer(paths, scanner, new FakeProjectLocator(), new NewFilePublisher()).Initialize(false)!;
        var workspace = new Workspace(paths, scanner);
        workspace.Load();
        workspace.EditPolicy(policy => policy with { Levels = [["Domain"]], Editor = "editor {file}" });

        workspace.GenerateOrReuse(initialization, inputsUnchanged: true);

        Assert.Equal(1, scanner.Calls);
        Assert.Same(initialization.Model, workspace.Model);
        Assert.Equal("editor {file}", workspace.Policy.Editor);
    }

    [Fact]
    public async Task AnExistingPolicyAlwaysScansAndDoesNotInvokeTheInitializationCheck()
    {
        using var project = new TempProject(("milligram.json", "{}"), ("Order.cs", "class Order { }"));
        var scanner = new CountingScanner();
        var workspace = new Workspace(new ProjectPaths(project.Root), scanner);
        workspace.Load();
        var events = new FakeEvents();

        await Actions(workspace, events, new JobQueue(events)).Start(null, () => throw new InvalidOperationException());

        Assert.Equal(1, scanner.Calls);
        Assert.Single(workspace.Model.Types);
    }

    [Fact]
    public async Task InputFreshnessIsCheckedWhenTheStartupJobRuns()
    {
        using var project = new TempProject(("Order.cs", "namespace Shop; public class Order { }"));
        var paths = new ProjectPaths(project.Root);
        var scanner = new CountingScanner();
        var initialization = new ProjectInitializer(paths, scanner, new FakeProjectLocator(), new NewFilePublisher()).Initialize(false)!;
        var workspace = new Workspace(paths, scanner);
        workspace.Load();
        var events = new FakeEvents();
        var jobs = new JobQueue(events);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preceding = jobs.Enqueue("preceding", async (_, _) => { await release.Task; return "done"; });
        var unchanged = true;
        var starting = Actions(workspace, events, jobs).Start(initialization, () => unchanged);
        project.Write("Order.cs", "namespace Shop; public class Changed { }");
        unchanged = false;
        release.SetResult();

        await Task.WhenAll(preceding, starting);

        Assert.Equal(2, scanner.Calls);
        Assert.Equal("Changed", Assert.Single(workspace.Model.Types).Name);
    }

    private static ViewerActions Actions(Workspace workspace, FakeEvents events, JobQueue jobs)
    {
        var projects = new FakeProjectLocator();
        var processes = new FakeProcessRunner();
        return new ViewerActions(workspace, new PolicyEditor(workspace),
            new CrapService(workspace, projects, processes, new FakeCoverageReader(new(new Dictionary<string, IReadOnlyDictionary<int, int>>()))),
            new MutationService(workspace, projects, processes, new FakeMutationReader()), jobs, new FakeCompanion(), events);
    }

    [Fact]
    public async Task ARedundantInputRefreshCannotHideThePreviousEvaluationFailure()
    {
        using var project = new TempProject(("Source.cs", "class Original {}"));
        var inputs = new EvaluatedInputs();
        inputs.AddFiles([Path.Combine(project.Root, "Source.cs")]);
        inputs.Complete();
        var scanner = new CountingScanner { Inputs = inputs };
        var workspace = new Workspace(new ProjectPaths(project.Root), scanner);
        workspace.Generate();
        var events = new FakeEvents();
        var jobs = new JobQueue(events);
        var actions = Actions(workspace, events, jobs);
        scanner.Fails = true;
        await actions.Regenerate();
        await actions.Regenerate("Input refresh", inputsOnly: true);
        Assert.Equal(JobState.Failed, jobs.Status.State);
        Assert.Equal("Invalid project import", jobs.Status.Message);
        Assert.Equal(2, scanner.Calls);
        Assert.Equal("Original", Assert.Single(workspace.Model.Types).Id);
        scanner.Fails = false;
        project.Write("Source.cs", "class Repaired {}");
        await actions.Regenerate("Input refresh", inputsOnly: true);
        Assert.Equal(JobState.Succeeded, jobs.Status.State);
        Assert.Equal("Repaired", Assert.Single(workspace.Model.Types).Id);
        Assert.Null(workspace.ScanError);
    }

    private sealed class CountingScanner : ILanguageScanner
    {
        private readonly CSharpScanner scanner = new();
        public int Calls { get; private set; }
        public IScanInputs? Inputs { get; init; }
        public bool Fails { get; set; }
        public Action? AfterFirstScan { get; init; }

        public CodeModel Scan(ScanRequest request, Action<string>? progress = null)
        {
            Calls++;
            if (Fails) throw new MilligramException("Invalid project import");
            var model = scanner.Scan(request, progress);
            if (Calls == 1) AfterFirstScan?.Invoke();
            return model;
        }
    }
}
