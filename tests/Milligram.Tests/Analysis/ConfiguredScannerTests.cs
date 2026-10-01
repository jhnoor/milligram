using Milligram.Adapters.Files;
using Milligram.Analysis.CSharp;
using Milligram.Analysis.DotNet;
using Milligram.Application;
using Milligram.Domain.Model;
using Milligram.Domain.Policies;

namespace Milligram.Tests.Analysis;

public class ConfiguredScannerTests
{
    [Fact]
    public void InvocationOverridesChangeOnlyTheSuppliedScanSettings()
    {
        var persisted = new ScanSettings { Mode = ScanMode.SourceOnly, Projects = ["Old.csproj"], Configuration = "Debug" };
        var explicitProject = persisted.Override(new ScanSettings { Mode = ScanMode.Msbuild, Projects = ["New.csproj"] });
        Assert.Equal(ScanMode.Msbuild, explicitProject.Mode);
        Assert.Equal(["New.csproj"], explicitProject.Projects);
        Assert.Equal("Debug", explicitProject.Configuration);
        var configuration = persisted.Override(new ScanSettings { Configuration = "Release" });
        Assert.Equal(ScanMode.SourceOnly, configuration.Mode);
        Assert.Equal(["Old.csproj"], configuration.Projects);
        Assert.Equal("Release", configuration.Configuration);
        Assert.Equal(ScanMode.SourceOnly, explicitProject.Override(new ScanSettings { Mode = ScanMode.SourceOnly }).Mode);
    }

    [Fact]
    public void DiscoveryKeepsAncestorsAndSelectedSubtreesIncludingMalformedProjects()
    {
        using var root = new TempProject(("Root.csproj", "<Project/>"), ("src/App/App.csproj", "broken"),
            ("tests/Tests.csproj", "<Project/>"), ("src/Skip/Skip.csproj", "<Project/>"));
        var request = new ScanRequest(root.Root, Path.Combine(root.Root, "src"), ["src/Skip/**"], "", [], "Test");
        var files = ConfiguredScanner.Discover(request, new DotNetProjectLocator().Files(root.Root));
        Assert.Equal(["Root.csproj", "src/App/App.csproj"], files);
    }

    [Fact]
    public void PolicyChangesSwitchScannersAndFailuresKeepThePreviousInputMonitor()
    {
        using var root = new TempProject(("Source.cs", "class Source {}"), ("App.csproj", "<Project/>"));
        var inputs = new EvaluatedInputs();
        inputs.AddDirectories([root.Root]);
        inputs.Complete();
        var calls = new List<(IReadOnlyList<string> Files, string? Configuration)>();
        var fail = false;
        var scanner = new ConfiguredScanner(new CSharpScanner(), new DotNetProjectLocator(), (files, configuration) =>
        {
            calls.Add((files, configuration));
            return new EvaluatedScanner(inputs, () => fail);
        });
        var paths = new ProjectPaths(root.Root);
        var workspace = new Workspace(paths, scanner);
        workspace.EditPolicy(policy => policy with { Scan = new ScanSettings { Projects = ["App.csproj"], Configuration = "Release" } });
        workspace.Generate();
        Assert.Equal("Release", Assert.Single(calls).Configuration);
        Assert.Equal(["App.csproj"], calls[0].Files);
        Assert.Same(inputs, workspace.ScanInputs);
        var before = File.ReadAllText(paths.ModelFile);
        fail = true;
        Assert.Throws<MilligramException>(() => workspace.Generate());
        Assert.Equal(before, File.ReadAllText(paths.ModelFile));
        Assert.Same(inputs, workspace.ScanInputs);
        workspace.EditPolicy(policy => policy with { Scan = new ScanSettings { Mode = ScanMode.SourceOnly } });
        workspace.Generate();
        Assert.Equal("Source", Assert.Single(workspace.Model.Types).Id);
        Assert.Null(workspace.ScanInputs);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public void OnlyAutoMayUseSourceFilesWhenNoProjectExists()
    {
        using var root = new TempProject(("Source.cs", "class Source {}"));
        var scanner = new ConfiguredScanner(new CSharpScanner(), new DotNetProjectLocator(), (_, _) => throw new InvalidOperationException());
        var request = new ScanRequest(root.Root, root.Root, [], "", [], "Test");
        var messages = new List<string>();
        Assert.Single(scanner.Scan(request, messages.Add).Types);
        Assert.Contains(messages, message => message.Contains("source-only", StringComparison.Ordinal));
        Assert.Throws<MilligramException>(() => scanner.Scan(request with { Scan = new ScanSettings { Mode = ScanMode.Msbuild } }));
    }

    [Fact]
    public void ConfigurationOverridesKeepThePersistedEntryProjects()
    {
        using var project = new TempProject(("milligram.json", """{ "scan": { "mode": "msbuild", "projects": ["Selected.csproj"], "configuration": "Debug" } }"""));
        var calls = new List<(IReadOnlyList<string> Files, string? Configuration)>();
        var scanner = new ConfiguredScanner(new CSharpScanner(), new DotNetProjectLocator(), (files, configuration) =>
        {
            calls.Add((files, configuration));
            return new CSharpScanner();
        });
        var workspace = new Workspace(new ProjectPaths(project.Root), scanner, new ScanSettings { Configuration = "Release" });
        workspace.Load();
        workspace.Generate();
        Assert.Equal("Release", Assert.Single(calls).Configuration);
        Assert.Equal(["Selected.csproj"], calls[0].Files);
        Assert.Equal("Debug", workspace.Policy.Scan.Configuration);
    }

    [Fact]
    public void AutomaticDiscoveryNoticesANewProjectOutsideTheOriginalCompilerInputs()
    {
        using var project = new TempProject(("src/App/App.csproj", "<Project/>"));
        var initial = new EvaluatedInputs();
        initial.AddFiles([Path.Combine(project.Root, "src/App/App.csproj")]);
        initial.Complete();
        var scanner = new ConfiguredScanner(new CSharpScanner(), new DotNetProjectLocator(), (_, _) => new EvaluatedScanner(initial, () => false));
        scanner.Scan(new ScanRequest(project.Root, Path.Combine(project.Root, "src"), [], "", [], "Test"));
        var inputs = scanner.Inputs!;
        Assert.Equal(inputs.Version, inputs.ReadVersion());
        project.Write("src/Other/Other.csproj", "<Project/>");
        Assert.NotEqual(inputs.Version, inputs.ReadVersion());
    }

    private sealed class EvaluatedScanner(IScanInputs inputs, Func<bool> fail) : ILanguageScanner
    {
        public IScanInputs Inputs => inputs;
        public CodeModel Scan(ScanRequest request, Action<string>? progress = null) =>
            fail() ? throw new MilligramException("Evaluation failed") : CodeModel.Empty("Evaluated", "");
    }
}
