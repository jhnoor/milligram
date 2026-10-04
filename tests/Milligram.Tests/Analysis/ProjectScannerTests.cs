using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Milligram.Analysis.CSharp;
using Milligram.Application;
using Milligram.Domain.Metrics;
using Milligram.Domain.Model;

namespace Milligram.Tests.Analysis;

public class ProjectScannerTests
{
    [Theory]
    [InlineData("configuration")]
    [InlineData("symbols")]
    [InlineData("optimization")]
    [InlineData("framework")]
    [InlineData("unresolved")]
    public void AChangedCompilerContextMakesExistingMetricsStaleEvenWhenSourceIsUnchanged(string change)
    {
        using var root = new TempProject();
        var project = Project(root, "App", [("A.cs", "public class A { public int Read() => 1; }")], ["DEBUG", "TRACE"]);
        var request = Request(root);
        var original = ProjectScanner.Collect([project], request, _ => { });
        var type = Assert.Single(original.Types);
        var member = Assert.Single(type.Members);
        var hits = new LineHits(new Dictionary<string, IReadOnlyDictionary<int, int>> { ["A.cs"] = new Dictionary<int, int> { [1] = 1 } });
        var crap = Crap.Compute(original, hits, DateTimeOffset.UtcNow);
        var mutation = MutationMapper.Merge(MutationSnapshot.Empty, original, [], [member.Id], ["A.cs"], DateTimeOffset.UtcNow);
        Assert.False(TypeMetrics.Crap(type, crap)!.Stale);
        Assert.False(TypeMetrics.Mutation(type, mutation)!.Stale);

        var reordered = Project(root, "App", [("A.cs", "public class A { public int Read() => 1; }")], ["TRACE", "DEBUG"]);
        Assert.Equal(member.Hash, Assert.Single(Assert.Single(Scan(root, [reordered]).Types).Members).Hash);
        if (change == "configuration") request = request with { Scan = request.Scan with { Configuration = "Release" } };
        else if (change == "symbols") project = Project(root, "App", [("A.cs", "public class A { public int Read() => 1; }")], ["TRACE"]);
        else if (change == "framework") project = project with { Framework = "net10.0" };
        else if (change == "unresolved") project = project with { ContextResolved = false };
        else project = project with { Compilation = project.Compilation.WithOptions(project.Compilation.Options.WithOptimizationLevel(OptimizationLevel.Release)) };
        var changed = Assert.Single(ProjectScanner.Collect([project], request, _ => { }).Types);
        Assert.True(TypeMetrics.Crap(changed, crap)!.Stale);
        Assert.True(TypeMetrics.Mutation(changed, mutation)!.Stale);
        Assert.Single(MutationMapper.Changed(mutation, changed.Members));
    }

    [Fact]
    public void FailedEvaluationPublishesTheNewInputMonitorInsteadOfThePreviousCompilationSnapshot()
    {
        using var root = new TempProject(("App.csproj", "<Project/>"));
        var previous = new Milligram.Adapters.Files.EvaluatedInputs().Complete();
        var next = new Milligram.Adapters.Files.EvaluatedInputs().Complete();
        IScanInputs? observed = null;
        var project = Project(root, "App", [("A.cs", "class A {}")]) with { Inputs = previous };
        var scanner = new ProjectScanner((_, _, _) =>
        {
            if (observed is not null) { observed = next; throw new InvalidOperationException("missing import"); }
            observed = previous;
            return Task.FromResult<IReadOnlyList<ProjectCompilation>>([project]);
        }, ["App.csproj"], inputState: () => observed);
        scanner.Scan(Request(root));
        Assert.Same(previous, scanner.Inputs);
        Assert.Throws<MilligramException>(() => scanner.Scan(Request(root)));
        Assert.Same(next, scanner.Inputs);
        var withoutProvider = new ProjectScanner((_, _, _) => Task.FromResult<IReadOnlyList<ProjectCompilation>>([project]), ["App.csproj"]);
        withoutProvider.Scan(Request(root));
        Assert.Same(previous, withoutProvider.Inputs);
    }

    [Fact]
    public void AProjectWithDuplicateDeclarationsReportsTheSourceErrorInsteadOfAskingForEvaluation()
    {
        using var root = new TempProject();
        var project = Project(root, "App", [("A.cs", "public delegate void Callback(); public delegate void Callback(int value);")]);
        var log = new List<string>();
        var model = ProjectScanner.Collect([project], Request(root), log.Add);

        Assert.Equal(2, model.Types.Count);
        Assert.Contains(log, line => line.Contains("check the project's duplicate declarations", StringComparison.Ordinal));
        Assert.DoesNotContain(log, line => line.Contains("still needs MSBuild", StringComparison.Ordinal));
    }

    [Fact]
    public void GlobalNamespaceTypesKeepProjectReferenceEdges()
    {
        using var root = new TempProject();
        var library = Project(root, "Library", [("Target.cs", "public class Target<T> {}")]);
        var app = Project(root, "App", [("App.cs", "public class Consumer { public Target<int> Value = new(); }")], references: [library.Compilation.ToMetadataReference()]);
        Assert.Contains(Scan(root, [app, library]).Edges, edge => edge.From == "Consumer" && edge.To == "Target<T>");
    }

    [Fact]
    public void ProjectsKeepTheirOwnSymbolsImportsAndSourceMembership()
    {
        using var root = new TempProject(("Unused.cs", "public class Unlisted {}"));
        var left = Project(root, "Left", [("Shared.cs", """
            #if LEFT
            namespace Left; public class Active { public HttpClient Client = new(); }
            #else
            namespace Left; public class Inactive {}
            #endif
            """), ("Usings.cs", "global using System.Net.Http;")], ["LEFT"]);
        var right = Project(root, "Right", [("Right.cs", "namespace Right; public class Active { public HttpClient Client = new(); }")]);
        var model = Scan(root, [right, left]);

        Assert.Equal(["Left.Active", "Right.Active"], model.Types.Select(type => type.Id));
        Assert.Contains(model.Edges, edge => edge.From == "Left.Active" && edge.To == "x:System.Net.Http");
        Assert.DoesNotContain(model.Edges, edge => edge.From == "Right.Active");
    }

    [Fact]
    public void ProjectReferencesResolveToTheRightDuplicateIncludingGenericTypes()
    {
        using var root = new TempProject();
        var library = Project(root, "Library", [("Library.cs", "namespace Shared; public class Item<T> { public T Value = default!; }")]);
        var unrelated = Project(root, "Unrelated", [("Other.cs", "namespace Shared; public class Item<T> { public T Different = default!; }")]);
        unrelated = unrelated with { Compilation = unrelated.Compilation.WithAssemblyName("Library") };
        var app = Project(root, "App", [("App.cs", "namespace App; public class Consumer { public Shared.Item<int> Value = new(); }")], references: [library.Compilation.ToMetadataReference()]);
        var model = Scan(root, [unrelated, app, library]);
        var expected = model.Types.Single(type => type.File == "Library.cs");
        var other = model.Types.Single(type => type.File == "Other.cs");

        Assert.NotEqual(expected.Id, other.Id);
        Assert.Contains("@project=Library.csproj%5BLibrary%5D", expected.Id, StringComparison.Ordinal);
        Assert.StartsWith(expected.Id + ".", expected.Members[0].Id, StringComparison.Ordinal);
        Assert.Contains(model.Edges, edge => edge.From == "App.Consumer" && edge.To == expected.Id);
        Assert.DoesNotContain(model.Edges, edge => edge.To == other.Id);
    }

    [Fact]
    public void LinkedDeclarationsKeepSeparateProjectIdentitiesAndDependencies()
    {
        using var root = new TempProject();
        const string shared = "namespace Shared; public class Item { public Target Value = new(); }";
        var left = Project(root, "Left", [("Linked.cs", shared), ("Left.cs", "global using Target = Left.Target; namespace Left; public class Target {}")]);
        var right = Project(root, "Right", [("Linked.cs", shared), ("Right.cs", "global using Target = Right.Target; namespace Right; public class Target {}")]);
        var model = Scan(root, [right, left]);
        var linked = model.Types.Where(type => type.File == "Linked.cs").ToList();

        Assert.Equal(2, linked.Count);
        var first = linked.Single(type => type.Id.Contains("Left.csproj", StringComparison.Ordinal));
        var second = linked.Single(type => type.Id.Contains("Right.csproj", StringComparison.Ordinal));
        Assert.Contains(model.Edges, edge => edge.From == first.Id && edge.To == "Left.Target");
        Assert.Contains(model.Edges, edge => edge.From == second.Id && edge.To == "Right.Target");
        Assert.DoesNotContain(model.Edges, edge => edge.From == first.Id && edge.To == "Right.Target");
        Assert.Equal(model.Types.Select(type => type.Id), Scan(root, [left, right]).Types.Select(type => type.Id));
    }

    [Fact]
    public void ExcludedDeclarationsStillBindButAreNotForeignNodesOrDependencies()
    {
        using var root = new TempProject();
        var library = Project(root, "Library", [("Hidden.cs", "namespace Hidden; public class Target {}")]);
        var app = Project(root, "App", [("LinkedOutsideSrc.cs", "namespace App; public class Consumer { public Hidden.Target Value = new(); }")], references: [library.Compilation.ToMetadataReference()]);
        var request = Request(root) with { SourceDirectory = Path.Combine(root.Root, "src"), Exclude = ["Hidden.cs"], DiscoverForeign = true };
        var model = ProjectScanner.Collect([library, app], request, _ => { });

        Assert.Equal("App.Consumer", Assert.Single(model.Types).Id);
        Assert.Empty(model.Edges);
        Assert.DoesNotContain(model.Foreign, node => node.Id == "x:Hidden");
    }

    [Fact]
    public void OutsideSourceAndReferencesNeverBecomeSourceCards()
    {
        using var root = new TempProject();
        using var outside = new TempProject();
        var reference = Project(outside, "Outside", [("Target.cs", "namespace Outside; public class Target {}")]);
        var app = Project(root, "App", [("App.cs", "namespace App; public class Consumer { public Outside.Target Value = new(); }")], references: [reference.Compilation.ToMetadataReference()]);
        var options = new CSharpParseOptions(LanguageVersion.Preview);
        app = app with
        {
            Compilation = app.Compilation.AddSyntaxTrees(
                CSharpSyntaxTree.ParseText("public class Escaped {}", options, path: Path.Combine(outside.Root, "Escaped.cs")),
                CSharpSyntaxTree.ParseText("public class Generated {}", options, path: "Generated.cs"),
                CSharpSyntaxTree.ParseText("public class Missing {}", options, path: Path.Combine(root.Root, "Missing.cs")))
        };
        var log = new List<string>();
        var model = ProjectScanner.Collect([reference, app], Request(root), log.Add);

        Assert.Equal("App.Consumer", Assert.Single(model.Types).Id);
        Assert.Contains(log, line => line.Contains("Reference project outside", StringComparison.Ordinal));
        Assert.Equal(3, log.Count(line => line.Contains("Source outside", StringComparison.Ordinal)));
    }

    [Fact]
    public void AScanPassesDistinctSortedAbsoluteProjectsAndReportsItsMeasuredCompletion()
    {
        using var root = new TempProject(("App.csproj", "<Project/>"), ("B.csproj", "<Project/>"));
        var project = Project(root, "App", [("Source.cs", "namespace App; public class Source {}")]);
        var log = new List<string>();
        var scanner = new ProjectScanner((files, configuration, report) =>
        {
            Assert.Equal([Path.Combine(root.Root, "App.csproj"), Path.Combine(root.Root, "B.csproj")], files);
            Assert.Equal("Release", configuration);
            report("Evaluated.");
            return Task.FromResult<IReadOnlyList<ProjectCompilation>>([project]);
        }, ["B.csproj", "App.csproj", "App.csproj"], "Release");
        var model = scanner.Scan(Request(root), log.Add);

        Assert.Single(model.Types);
        Assert.Contains("Evaluated.", log);
        Assert.Contains(log, line => line.StartsWith("Scanned 1 types and 0 dependencies from 1 project context(s) in ", StringComparison.Ordinal));
        Assert.Equal("Title", model.Title);
        Assert.Equal("App", model.Prefix);
    }

    [Theory]
    [InlineData("missing.csproj")]
    [InlineData("File.txt")]
    [InlineData("../Outside.csproj")]
    public void InvalidEntryFilesCannotReachEvaluation(string name)
    {
        using var root = new TempProject(("File.txt", "text"));
        var scanner = new ProjectScanner((_, _, _) => throw new Xunit.Sdk.XunitException("Evaluation must not run"), [name]);
        Assert.Contains("--msbuild must name", Assert.Throws<MilligramException>(() => scanner.Scan(Request(root))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedEvaluationPreservesThePreviousModelAndExplainsTheExplicitFallback()
    {
        using var root = new TempProject(("App.csproj", "<Project/>"));
        var paths = new ProjectPaths(root.Root);
        var original = CodeModel.Empty("Previous", "");
        JsonFile.Write(paths.ModelFile, original);
        var scanner = new ProjectScanner((_, _, _) => throw new InvalidOperationException("SDK unavailable"), ["App.csproj"]);
        var workspace = new Milligram.Application.Workspace(paths, scanner);
        workspace.Load();

        var error = Assert.Throws<MilligramException>(() => workspace.Generate());
        Assert.Contains("SDK unavailable", error.Message, StringComparison.Ordinal);
        Assert.Contains("--source-only", error.Message, StringComparison.Ordinal);
        Assert.Equal("Previous", workspace.Model.Title);
        Assert.Equal("Previous", JsonFile.Read<CodeModel>(paths.ModelFile)!.Title);
    }

    [Fact]
    public void EmptyEvaluationDoesNotEraseTheModel()
    {
        using var root = new TempProject(("App.csproj", "<Project/>"));
        var scanner = new ProjectScanner((_, _, _) => Task.FromResult<IReadOnlyList<ProjectCompilation>>([]), ["App.csproj"]);
        Assert.Contains("no C# projects", Assert.Throws<MilligramException>(() => scanner.Scan(Request(root))).Message, StringComparison.Ordinal);
        Assert.Throws<MilligramException>(() => new ProjectScanner((_, _, _) => throw new InvalidOperationException(), []).Scan(Request(root)));
    }

    private static ProjectCompilation Project(TempProject root, string name, (string File, string Text)[] sources,
        string[]? symbols = null, MetadataReference[]? references = null)
    {
        var trees = sources.Select(source => CSharpSyntaxTree.ParseText(source.Text,
            new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: symbols), root.Write(source.File, source.Text)));
        var compilation = CSharpCompilation.Create(name, trees, References.For([]).Concat(references ?? []), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new ProjectCompilation(Path.Combine(root.Root, name + ".csproj"), name, compilation);
    }

    private static ScanRequest Request(TempProject root) => new(root.Root, root.Root, [], "App", ["System.Net.Http"], "Title");

    private static CodeModel Scan(TempProject root, IReadOnlyList<ProjectCompilation> projects) => ProjectScanner.Collect(projects, Request(root), _ => { });
}
