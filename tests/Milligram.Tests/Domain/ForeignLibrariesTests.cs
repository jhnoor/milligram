using Milligram.Domain.Hierarchy;
using Milligram.Domain.Model;

namespace Milligram.Tests.Domain;

public class ForeignLibrariesTests
{
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
