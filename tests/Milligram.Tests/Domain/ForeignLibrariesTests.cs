using Milligram.Domain.Hierarchy;
using Milligram.Domain.Model;

namespace Milligram.Tests.Domain;

public class ForeignLibrariesTests
{
    [Fact]
    public void SelectedLibrariesMergeCountsAndStrengthWhileKeepingInternalEdges()
    {
        var model = Build.Model([Build.Type("App.A"), Build.Type("App.B")],
            [Build.Edge("App.B", "x:Vendor.Api.Nested", EdgeKind.Implements, 2),
             Build.Edge("App.B", "x:Vendor.Api", EdgeKind.Dependency, 3),
             Build.Edge("App.A", "x:Vendor.Api.Special", EdgeKind.Inheritance, 4),
             Build.Edge("App.A", "x:Vendor.ApiExtra", count: 5),
             Build.Edge("App.B", "x:System.Collections", count: 6),
             Build.Edge("App.A", "App.B", count: 7)],
            [new("x:Vendor.Api.Nested", "Vendor.Api.Nested"), new("x:Vendor.Api", "Vendor.Api"),
             new("x:Vendor.Api.Special", "Vendor.Api.Special"), new("x:Vendor.ApiExtra", "Vendor.ApiExtra"),
             new("x:System.Collections", "System.Collections")]);

        var selected = ForeignLibraries.Select(model, ["", "Vendor.Api", "Vendor.Api.Special", "Unused"]);

        Assert.Same(model.Types, selected.Types);
        Assert.Equal(model.Title, selected.Title);
        Assert.Equal(model.Prefix, selected.Prefix);
        Assert.Equal(model.GeneratedAt, selected.GeneratedAt);
        Assert.Equal([new("x:Vendor.Api", "Vendor.Api"), new ForeignNode("x:Vendor.Api.Special", "Vendor.Api.Special")], selected.Foreign);
        Assert.Equal([Build.Edge("App.A", "App.B", count: 7),
            Build.Edge("App.A", "x:Vendor.Api.Special", EdgeKind.Inheritance, 4),
            Build.Edge("App.B", "x:Vendor.Api", EdgeKind.Implements, 5)], selected.Edges);
        Assert.Equal(selected.Foreign, ForeignLibraries.Select(model with { Foreign = model.Foreign.Reverse().ToList() }, ["Vendor.Api.Special", "Vendor.Api"]).Foreign);
        Assert.Equal(selected.Edges, ForeignLibraries.Select(model with { Edges = model.Edges.Reverse().ToList() }, ["Vendor.Api.Special", "Vendor.Api"]).Edges);
        Assert.Equal([Build.Edge("App.A", "App.B", count: 7)], ForeignLibraries.Select(model, []).Edges);
        Assert.Empty(ForeignLibraries.Select(model, []).Foreign);
    }

    [Theory]
    [InlineData("Microsoft.CodeAnalysis.CSharp.Syntax", "Microsoft.CodeAnalysis")]
    [InlineData("Microsoft.AspNetCore.Builder", "Microsoft.AspNetCore")]
    [InlineData("Newtonsoft.Json.Linq", "Newtonsoft.Json")]
    [InlineData("Xunit", "Xunit")]
    [InlineData("System.Text.Json.Serialization", "System.Text.Json")]
    [InlineData("System.Text.RegularExpressions", "System.Text.RegularExpressions")]
    [InlineData("System.IO", "System.IO")]
    [InlineData("System.Runtime.InteropServices", "System.Runtime.InteropServices")]
    [InlineData("System.LinqTools", "System.LinqTools")]
    [InlineData("Systematic.Library.Api", "Systematic.Library")]
    [InlineData("System", null)]
    [InlineData("", null)]
    [InlineData("System.Collections", null)]
    [InlineData("System.Collections.Generic", null)]
    [InlineData("System.Linq", null)]
    [InlineData("System.Linq.Expressions", null)]
    [InlineData("System.Threading.Tasks", null)]
    [InlineData("System.Runtime.CompilerServices", null)]
    [InlineData("System.Diagnostics.CodeAnalysis", null)]
    public void GroupsLibrariesAndLeavesOutRoutineNamespaces(string ns, string? expected)
    {
        var model = Build.Model([Build.Type("App.A")], [Build.Edge("App.A", "x:" + ns)], [new ForeignNode("x:" + ns, ns)]);
        Assert.Equal(expected is null ? [] : new[] { expected }, ForeignLibraries.Suggest(model));
    }

    [Fact]
    public void CountsDistinctTypesAcrossNamespacesAndCapsSuggestionsWithStableTies()
    {
        var libraries = Enumerable.Range(0, 10).Select(i => $"Vendor.Library{i}").ToList();
        var foreign = libraries.Select(n => new ForeignNode("x:" + n, n)).ToList();
        foreign.Add(new ForeignNode("x:Vendor.Library9.Nested", "Vendor.Library9.Nested"));
        var edges = libraries.Select(n => Build.Edge("App.A", "x:" + n, count: 100)).ToList();
        edges.Add(Build.Edge("App.A", "x:Vendor.Library8", count: 999));
        edges.Add(Build.Edge("App.A", "x:Vendor.Library8", count: 999));
        edges.Add(Build.Edge("App.B", "x:Vendor.Library9.Nested"));
        edges.Add(Build.Edge("App.A", "App.B"));
        foreign.Add(new ForeignNode("x:Unused.Library", "Unused.Library"));
        var model = Build.Model([Build.Type("App.A"), Build.Type("App.B")], edges, foreign);

        string[] expected = [.. libraries.Take(7), "Vendor.Library9"];
        Assert.Equal(expected, ForeignLibraries.Suggest(model));
        Assert.Equal(expected, ForeignLibraries.Suggest(model with { Edges = edges.AsEnumerable().Reverse().ToList(), Foreign = foreign.AsEnumerable().Reverse().ToList() }));
    }
}
