using Milligram.Adapters.Files;
using Milligram.Analysis.CSharp;
using Milligram.Application;
using Milligram.Domain.Model;

namespace Milligram.Tests.Analysis;

public class TypeIdentityTests
{
    [Fact]
    public void FileLocalTypesKeepTheirOwnMembersDependenciesAndCards()
    {
        using var project = new TempProject(
            ("A.cs", "namespace Shop; public class First { private Helper Make() => new(); } file class Helper { public First Get() => new(); }"),
            ("B.cs", "namespace Shop; public class Second { private Helper Make() => new(); } file class Helper { public Second Get() => new(); }"));
        var paths = new ProjectPaths(project.Root);
        var scanner = new CSharpScanner();
        Assert.NotNull(new ProjectInitializer(paths, scanner, new FakeProjectLocator(), new NewFilePublisher()).Initialize(force: false));
        var workspace = new Workspace(paths, scanner);
        workspace.Load();
        var model = workspace.Generate();
        var first = model.Types.Single(t => t.Name == "Helper" && t.File == "A.cs");
        var second = model.Types.Single(t => t.Name == "Helper" && t.File == "B.cs");

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(4, model.Types.Select(t => t.Id).Distinct().Count());
        Assert.Equal("Shop", first.Namespace);
        Assert.NotEqual(Assert.Single(first.Members).Id, Assert.Single(second.Members).Id);
        Assert.Equal("A.cs", first.Members[0].Span.File);
        Assert.Equal("B.cs", second.Members[0].Span.File);
        Assert.Contains(model.Edges, e => e.From == "Shop.First" && e.To == first.Id);
        Assert.Contains(model.Edges, e => e.From == "Shop.Second" && e.To == second.Id);
        Assert.Contains(model.Edges, e => e.From == first.Id && e.To == "Shop.First");
        Assert.Contains(model.Edges, e => e.From == second.Id && e.To == "Shop.Second");
        Assert.DoesNotContain(model.Edges, e => e.From == "Shop.First" && e.To == second.Id);
        Assert.DoesNotContain(model.Edges, e => e.From == "Shop.Second" && e.To == first.Id);
        Assert.NotEmpty(workspace.View("real", null).Nodes);
        Assert.All(model.Types, type => Assert.NotNull(workspace.Card("real", type.Id)));
    }

    [Fact]
    public void FileLocalIdsSurviveAnotherFileAndChangesBeforeTheDeclaration()
    {
        using var project = new TempProject(("A.cs", "namespace Shop; file class Helper { public void Run() {} }"));
        var before = Assert.Single(Scan(project).Types);
        project.Write("A.cs", "// moved down\n\nnamespace Shop; file class Helper { public void Run() {} }");
        project.Write("B.cs", "namespace Shop; file class Helper { public void Run() {} }");
        var after = Scan(project);

        var same = after.Types.Single(t => t.File == "A.cs");
        Assert.Equal(before.Id, same.Id);
        Assert.Equal(before.Members[0].Id, same.Members[0].Id);
        Assert.NotEqual(before.Id, after.Types.Single(t => t.File == "B.cs").Id);
    }

    [Fact]
    public void RepeatedDelegatesInOneFileHaveDistinctEdgesAndADiagnostic()
    {
        using var project = new TempProject(("A.cs", """
            namespace Shop;
            public class First {}
            public class Second {}
            public delegate void Callback(First value);
            public delegate void Callback(Second value);
            """));
        var progress = new List<string>();
        var model = Scan(project, progress.Add);
        var callbacks = model.Types.Where(t => t.Name == "Callback").OrderBy(t => t.Spans[0].Start).ToList();

        Assert.Equal(2, callbacks.Count);
        Assert.Equal(["Shop.Callback@A.cs", "Shop.Callback@A.cs#2"], callbacks.Select(type => type.Id));
        Assert.NotEqual(callbacks[0].Id, callbacks[1].Id);
        Assert.Contains(model.Edges, e => e.From == callbacks[0].Id && e.To == "Shop.First");
        Assert.Contains(model.Edges, e => e.From == callbacks[1].Id && e.To == "Shop.Second");
        Assert.DoesNotContain(model.Edges, e => e.From == callbacks[0].Id && e.To == "Shop.Second");
        Assert.Contains(progress, line => line.Contains("Shop.Callback", StringComparison.Ordinal));
        Assert.Contains(progress, line => line.Contains("MSBuild", StringComparison.Ordinal));

        project.Write("A.cs", File.ReadAllText(Path.Combine(project.Root, "A.cs")) + "\npublic delegate void Callback();");
        var extended = Scan(project).Types.Where(t => t.Name == "Callback").OrderBy(t => t.Spans[0].Start).ToList();
        Assert.Equal(3, extended.Select(t => t.Id).Distinct().Count());
        Assert.Equal(callbacks.Select(t => t.Id), extended.Take(2).Select(t => t.Id));
    }

    [Fact]
    public void QualifiedIdsCannotCollideWithOrdinaryNamespaceNames()
    {
        using var project = new TempProject(
            ("A.cs", "namespace Shop; file class Helper {}"),
            ("B.cs", "namespace Shop.HelperA; public class cs {}"));
        var model = Scan(project);

        Assert.Equal(2, model.Types.Select(t => t.Id).Distinct().Count());
        Assert.Single(model.Types, t => t.Id == "Shop.HelperA.cs");
    }

    [Fact]
    public void IdsAreIndependentOfTheAbsoluteCheckoutPath()
    {
        (string, string)[] files =
        [
            ("one/A #.cs", "namespace Shop; file class Helper { public void Run() {} } public delegate void Callback();"),
            ("two/A #.cs", "namespace Shop; file class Helper { public void Run() {} } public delegate void Callback(int value);"),
        ];
        using var left = new TempProject(files);
        using var right = new TempProject(files.Reverse().ToArray());
        var first = Scan(left);
        var second = Scan(right);

        Assert.Equal(4, first.Types.Select(t => t.Id).Distinct().Count());
        Assert.Equal(first.Types.Select(t => t.Id), second.Types.Select(t => t.Id));
        Assert.Equal(first.Types.SelectMany(t => t.Members).Select(m => m.Id), second.Types.SelectMany(t => t.Members).Select(m => m.Id));
        Assert.DoesNotContain(first.Types, t => t.Id.Contains(left.Root, StringComparison.Ordinal));
    }

    [Fact]
    public void OrdinaryAndPartialTypesKeepTheirExistingIdentities()
    {
        using var project = new TempProject(
            ("A.cs", "namespace Shop; public partial class Shared { public void First() {} } file class Helper {}"),
            ("B.cs", "namespace Shop; public partial class Shared { public void Second() {} } public class Helper {}"));
        var progress = new List<string>();
        var model = Scan(project, progress.Add);
        var shared = model.Types.Single(t => t.Id == "Shop.Shared");

        Assert.Equal(2, shared.Spans.Count);
        Assert.Equal(["Shop.Shared.First()", "Shop.Shared.Second()"], shared.Members.Select(m => m.Id));
        Assert.Single(model.Types, t => t.Id == "Shop.Helper" && t.File == "B.cs");
        Assert.NotEqual("Shop.Helper", model.Types.Single(t => t.File == "A.cs" && t.Name == "Helper").Id);
        Assert.DoesNotContain(progress, line => line.Contains("MSBuild", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    public void RepeatedNameDiagnosticsAreBoundedAndSorted(int count)
    {
        var declarations = "namespace Shop; " + string.Join(" ", Enumerable.Range(0, count).Reverse().Select(n => $"public delegate void D{n:D2}();"));
        using var project = new TempProject(("A.cs", declarations), ("B.cs", declarations));
        var progress = new List<string>();

        Assert.Equal(count * 2, Scan(project, progress.Add).Types.Select(t => t.Id).Distinct().Count());

        var names = progress.Where(line => line.StartsWith("  Repeated source type: ", StringComparison.Ordinal)).ToList();
        Assert.Equal(10, names.Count);
        Assert.Equal("  Repeated source type: Shop.D00", names[0]);
        Assert.Equal("  Repeated source type: Shop.D09", names[^1]);
        if (count > 10) Assert.Contains("  and 2 more repeated names.", progress);
        else Assert.DoesNotContain(progress, line => line.StartsWith("  and ", StringComparison.Ordinal));
    }

    private static CodeModel Scan(TempProject project, Action<string>? progress = null) =>
        new CSharpScanner().Scan(new ScanRequest(project.Root, project.Root, [], "Shop", [], "Shop"), progress);
}
