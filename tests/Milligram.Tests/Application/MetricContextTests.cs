using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Milligram.Adapters.Processes;
using Milligram.Analysis.CSharp;
using Milligram.Analysis.Coverage;
using Milligram.Application;
using Milligram.Domain.Metrics;
using Milligram.Domain.Model;
using Workspace = Milligram.Application.Workspace;

namespace Milligram.Tests.Application;

public class MetricContextTests
{
    private static readonly CompilerContext Net = new("Library/Library.csproj", "net10.0", "Release", "Library");
    private static readonly CompilerContext Standard = Net with { Framework = "netstandard2.0" };

    private static LineHits Hits(int hit) => new(new Dictionary<string, IReadOnlyDictionary<int, int>>
    {
        ["Shared/Value.cs"] = new Dictionary<int, int> { [4] = hit },
    });

    private static TypeNode Type(string id, CompilerContext context) => Build.Type(id,
        Build.Member(id, "Value", file: "Shared/Value.cs", startLine: 4, endLine: 4, hash: id)) with
    { Context = context };

    [Fact]
    public void ImportedCoverageCannotAssignOneAssemblyToTwoFrameworks()
    {
        var model = Build.Model([Type("Net", Net), Type("Standard", Standard)]);
        var hits = Hits(1) with { Modules = new Dictionary<string, LineHits> { ["Library"] = Hits(1) } };
        var log = new List<string>();

        var snapshot = CoverageAttribution.Compute(model, [new(hits, null)], DateTimeOffset.UtcNow, log.Add);

        Assert.Empty(snapshot.Members);
        Assert.Equal(2, log.Count);
        Assert.Contains(log, line => line.Contains("netstandard2.0", StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains("milligram crap", StringComparison.Ordinal));
    }

    [Fact]
    public void ATestRunOnlyScoresItsActualLibraryFrameworkAndCombinesMatchingRuns()
    {
        var model = Build.Model([Type("Net", Net), Type("Standard", Standard)]);
        var zero = Hits(0) with { Modules = new Dictionary<string, LineHits> { ["Library"] = Hits(0) } };
        var one = Hits(1) with { Modules = new Dictionary<string, LineHits> { ["Library"] = Hits(1) } };
        var snapshot = CoverageAttribution.Compute(model, [new(zero, [Standard]), new(one, [Standard])], DateTimeOffset.UtcNow, _ => { });

        Assert.Equal(1, Assert.Single(snapshot.Members).Value.Coverage);
        Assert.Contains("Standard.Value()", snapshot.Members.Keys);
        Assert.Null(TypeMetrics.Crap(model.Types[0], snapshot));
    }

    [Fact]
    public void CoberturaModulesKeepLinkedSourceCoverageSeparate()
    {
        using var project = new TempProject(("Shared/Value.cs", "// linked"));
        var report = project.Write("coverage.xml", """
            <coverage><packages>
              <package name="Left"><classes><class filename="Shared/Value.cs"><lines><line number="4" hits="1"/></lines></class></classes></package>
              <package name="Right"><classes><class filename="Shared/Value.cs"><lines><line number="4" hits="0"/></lines></class></classes></package>
            </packages></coverage>
            """);
        var left = Net with { Project = "Left/Left.csproj", Assembly = "Left" };
        var right = Net with { Project = "Right/Right.csproj", Assembly = "Right" };
        var hits = new CoberturaReader().Read([report], project.Root);
        var model = Build.Model([Type("Left", left), Type("Right", right)]);
        var snapshot = CoverageAttribution.Compute(model, [new(hits, null)], DateTimeOffset.UtcNow, _ => { });

        Assert.Equal(1, snapshot.Members["Left.Value()"].Coverage);
        Assert.Equal(0, snapshot.Members["Right.Value()"].Coverage);
    }

    [Fact]
    public void UnresolvedOwnershipAndMissingImportModulesRemainUnknown()
    {
        var unresolved = Net with { Resolved = false, Assembly = "Unknown" };
        var hits = Hits(1) with { Modules = new Dictionary<string, LineHits> { ["Other"] = Hits(1) } };
        var model = Build.Model([Type("Unknown", unresolved), Type("Standard", Standard)]);
        Assert.Empty(CoverageAttribution.Compute(model, [new(hits, null)], DateTimeOffset.UtcNow, _ => { }).Members);
        var automatic = CoverageAttribution.Compute(model, [new(hits, [unresolved, Standard])], DateTimeOffset.UtcNow, _ => { });
        Assert.Equal(0, Assert.Single(automatic.Members).Value.Coverage);
        Assert.Contains("Standard.Value()", automatic.Members.Keys);
    }

    [Fact]
    public void CoverageWithoutModuleNamesIsOnlyUsableForOneCompilerContext()
    {
        var model = Build.Model([Type("Net", Net), Type("Standard", Standard)]);
        Assert.Empty(CoverageAttribution.Compute(model, [new(Hits(1), null)], DateTimeOffset.UtcNow, _ => { }).Members);
        Assert.Single(CoverageAttribution.Compute(model, [new(Hits(1), [Net])], DateTimeOffset.UtcNow, _ => { }).Members);
    }

    [Fact]
    public void ScopedMutationPreservesOtherContextsAndUnscopedAmbiguityCannotMarkThemFresh()
    {
        var net = Type("Net", Net);
        var standard = Type("Standard", Standard);
        var model = Build.Model([net, standard]);
        var killed = new Mutant("Shared/Value.cs", 4, 10, MutantStatus.Killed, "test");
        var first = MutationMapper.Merge(MutationSnapshot.Empty, model, [killed], [net.Members[0].Id], [killed.File], DateTimeOffset.UtcNow, [net]);
        var second = MutationMapper.Merge(first, model, [killed with { Status = MutantStatus.Survived }], [standard.Members[0].Id], [killed.File], DateTimeOffset.UtcNow, [standard]);

        Assert.Equal(1, second.Members[net.Members[0].Id].Killed);
        Assert.Equal(1, second.Members[standard.Members[0].Id].Survived);
        Assert.Single(second.Gaps);
        var ambiguous = MutationMapper.Merge(MutationSnapshot.Empty, model, [killed], [net.Members[0].Id, standard.Members[0].Id], [killed.File], DateTimeOffset.UtcNow);
        Assert.Empty(ambiguous.Members);
    }

    [Fact]
    public void StrykerSelectionAccountsForConditionalReferencesAndFrameworkFallback()
    {
        var test = new CompilerContext("Tests/Tests.csproj", "net10.0", "Release", "Tests");
        var other = test with { Framework = "net9.0" };
        Assert.Single(MutationTests.Select(Standard, [new(test, [Standard])]));
        Assert.Empty(MutationTests.Select(Standard, [new(test, [Standard]), new(other, [Net])]));
        Assert.Empty(MutationTests.Select(Net, [new(test, [Standard]), new(other, [Net])]));
        Assert.Single(MutationTests.Select(Net, [new(test, [Net]), new(other, [Standard])]));
        Assert.Empty(MutationTests.Select(Net, [new(test with { Resolved = false }, [Net])]));
        Assert.Empty(MutationTests.Select(Net, [new(test, [Net with { Fingerprint = "different compiler flags" }])]));
    }

    [Fact]
    public void ATestReferenceWithDifferentCompilerOptionsCannotScoreTheScannedContext()
    {
        using var root = new TempProject();
        var paths = new ProjectPaths(root.Root);
        var syntax = CSharpSyntaxTree.ParseText("public class A {}", new CSharpParseOptions(preprocessorSymbols: ["LEFT"]));
        var project = new ProjectCompilation(root.Write("Library.csproj", "<Project/>"), "Library",
            CSharpCompilation.Create("Library", [syntax]))
        { Framework = "net10.0", Configuration = "Release" };
        var scanned = project.Context(paths);
        var reference = project with { Compilation = project.Compilation.WithOptions(project.Compilation.Options.WithOverflowChecks(true)) };
        var model = Build.Model([Type("Library", scanned)]);
        var hits = Hits(1) with { Modules = new Dictionary<string, LineHits> { ["Library"] = Hits(1) } };

        Assert.NotEqual(scanned.Fingerprint, reference.Context(paths).Fingerprint);
        Assert.Equal(scanned, (project with { Name = "Library(net10.0)" }).Context(paths, "Release"));
        Assert.Empty(CoverageAttribution.Compute(model, [new(hits, [reference.Context(paths)])], DateTimeOffset.UtcNow, _ => { }).Members);
    }

    [Fact]
    public void EvaluatedMetadataRequiresTheActualOutputAndDisambiguatesSharedOutputPaths()
    {
        var first = new EvaluatedProject("Library.csproj", "net10.0", "Release", "Library.dll");
        var second = first with { Framework = "netstandard2.0" };
        Assert.Equal(first, EvaluatedProject.Find([first], first.Path, "Library", first.Output));
        Assert.Equal(second, EvaluatedProject.Find([first, second], first.Path, "Library(netstandard2.0)", first.Output));
        Assert.Null(EvaluatedProject.Find([first, second], first.Path, "Library", first.Output));
        Assert.Null(EvaluatedProject.Find([first], first.Path, "Library", "Debug/Library.dll"));
        Assert.Null(EvaluatedProject.Find([first, first with { Configuration = "Debug" }], first.Path, "Library(net10.0)", first.Output));
    }

    [Fact]
    public void TestReachabilityUsesActualCompilerReferencesIncludingTransitiveDifferentFrameworks()
    {
        using var root = new TempProject();
        var paths = new ProjectPaths(root.Root);
        var library = new ProjectCompilation(root.Write("Library.csproj", "<Project/>"), "Library(netstandard2.0)", CSharpCompilation.Create("Library"))
        { Framework = "netstandard2.0", Configuration = "Release" };
        var other = library with { Name = "Library(net10.0)", Framework = "net10.0", Compilation = CSharpCompilation.Create("Library") };
        var facade = new ProjectCompilation(root.Write("Facade.csproj", "<Project/>"), "Facade",
            CSharpCompilation.Create("Facade", references: [library.Compilation.ToMetadataReference()]))
        { Framework = "net10.0" };
        var test = new ProjectCompilation(root.Write("Tests.csproj", "<Project/>"), "Tests",
            CSharpCompilation.Create("Tests", references: [facade.Compilation.ToMetadataReference()]))
        { Framework = "net10.0" };

        var graph = Assert.Single(ProcessRunner.TestContexts([other, library, facade, test], [test.Path], paths));

        Assert.Equal(test.Context(paths), graph.Context);
        Assert.Contains(library.Context(paths), graph.References);
        Assert.Contains(facade.Context(paths), graph.References);
        Assert.DoesNotContain(other.Context(paths), graph.References);
    }

    [Fact]
    public async Task LinkedSourcesAreMutatedInEachOwningProjectWithOnlyThatContextsMembers()
    {
        using var fixture = new LinkedFixture();
        var processes = new FakeProcessRunner((_, args, _) =>
        {
            var report = Path.Combine(FakeProcessRunner.After(args, "--output"), "reports", "mutation-report.json");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, "{}");
            return 0;
        });
        var service = new MutationService(fixture.Workspace, fixture.Locator, processes,
            new FakeMutationReader(new Mutant("Shared/Value.cs", 4, 20, MutantStatus.Killed, "Boolean")), fixture.Reader);

        var result = await service.RunAsync(["Shared/Value.cs"], false, _ => { }, CancellationToken.None);

        Assert.Equal(2, result.Projects);
        Assert.Empty(result.Skipped);
        Assert.Equal(2, result.Members);
        Assert.Equal(2, fixture.Workspace.Metrics.Mutation.Members.Values.Count(entry => entry.Killed == 1));
        Assert.Equal(["Left.csproj", "Right.csproj"], processes.Calls.Select(call => FakeProcessRunner.After(call.Args, "--project")));
        Assert.All(processes.Calls, call =>
        {
            Assert.Equal("net10.0", FakeProcessRunner.After(call.Args, "--target-framework"));
            Assert.Equal("Release", FakeProcessRunner.After(call.Args, "--configuration"));
            Assert.Single(FakeProcessRunner.AllAfter(call.Args, "--test-project"));
        });
        Assert.Equal(0, (await service.RunAsync(["Shared/Value.cs"], false, _ => { }, CancellationToken.None)).Projects);
    }

    [Fact]
    public async Task ATestFrameworkWithoutAReportCannotBorrowAnotherFrameworksCoverage()
    {
        using var fixture = new LinkedFixture(sharedTestProject: true);
        var processes = new FakeProcessRunner((_, args, _) =>
        {
            if (FakeProcessRunner.After(args, "--framework") == "net9.0") return 1;
            var output = FakeProcessRunner.After(args, "--results-directory");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "coverage.cobertura.xml"), "<coverage/>");
            return 0;
        });
        var hits = Hits(1) with { Modules = new Dictionary<string, LineHits> { ["Left"] = Hits(1) } };
        var service = new CrapService(fixture.Workspace, fixture.Locator, processes, new FakeCoverageReader(hits), fixture.Reader);

        var result = await service.RunAsync(null, _ => { }, CancellationToken.None);

        Assert.Equal(1, result.TestExitCode);
        Assert.Equal(1, result.Members);
        Assert.Equal(1, Assert.Single(fixture.Workspace.Metrics.Crap.Members).Value.Coverage);
        Assert.Equal(2, processes.Calls.Count);
        Assert.Equal(2, processes.Calls.Select(call => FakeProcessRunner.After(call.Args, "--results-directory")).Distinct().Count());
        Assert.Equal(["net10.0", "net9.0"], processes.Calls.Select(call => FakeProcessRunner.After(call.Args, "--framework")));
        Assert.Single(processes.Calls.Select(call => call.Args[1]).Distinct());
    }

    private sealed class Reader(IReadOnlyList<MetricTestProject> tests) : IMetricProjectReader
    {
        public Task<IReadOnlyList<MetricTestProject>> ReadAsync(string root, IReadOnlyList<string> paths, string? configuration,
            Action<string> log, CancellationToken cancellation) => Task.FromResult(tests);
    }

    private sealed class LinkedFixture : IDisposable
    {
        private readonly TempProject root = new(("milligram.json", "{}"));
        public Workspace Workspace { get; }
        public FakeProjectLocator Locator { get; }
        public IMetricProjectReader Reader { get; }

        public LinkedFixture(bool sharedTestProject = false)
        {
            const string source = "public class Shared\n{\n    public bool Value() =>\n#if LEFT\n        true;\n#else\n        false;\n#endif\n}";
            var path = root.Write("Shared/Value.cs", source);
            var projects = new[] { "Left", "Right" }.Select(name =>
            {
                var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(preprocessorSymbols: [name.ToUpperInvariant()]), path);
                return new ProjectCompilation(root.Write($"{name}/{name}.csproj", "<Project/>"), name,
                    CSharpCompilation.Create(name, [tree], References.For([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)))
                { Framework = sharedTestProject && name == "Right" ? "netstandard2.1" : "net10.0", Configuration = "Release" };
            }).ToList();
            var tests = projects.Select(project => new BuildProject(root.Write($"{project.Name}.Tests/Tests.csproj", "<Project/>"), project.Name + ".Tests", true, [project.Path])).ToList();
            var paths = new ProjectPaths(root.Root);
            Reader = new Reader(projects.Select((project, i) => new MetricTestProject(
                new(paths.Relative(tests[sharedTestProject ? 0 : i].Path), sharedTestProject && i == 1 ? "net9.0" : "net10.0", "Release", tests[i].Name), [project.Context(paths)])).ToList());
            Locator = new FakeProjectLocator(projects.Select(project => new BuildProject(project.Path, project.Name, false, [])).Concat(tests).ToArray());
            Workspace = new Workspace(paths, new ProjectScanner((_, _, _) => Task.FromResult<IReadOnlyList<ProjectCompilation>>(projects), projects.Select(project => project.Path).ToList()));
            Workspace.Load();
        }

        public void Dispose() => root.Dispose();
    }
}
