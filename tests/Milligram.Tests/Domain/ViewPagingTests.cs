using Milligram.Domain.Hierarchy;
using Milligram.Domain.Metrics;
using Milligram.Domain.Model;
using Milligram.Domain.Policies;
using Milligram.Domain.Views;

namespace Milligram.Tests.Domain;

public class ViewPagingTests
{
    private static DiagramView View(CodeModel model, Policy? policy = null, string? focus = null, int page = 0, string? query = null)
    {
        policy ??= new Policy { Prefix = "App" };
        return ViewBuilder.Build(model, policy, MetricsSet.Empty, TreeBuilder.Real(model, policy), focus, page, query);
    }

    [Fact]
    public void EveryNamespaceAndGlobalTypeIsReachableAcrossBoundedPages()
    {
        var model = Build.Model(Enumerable.Range(0, 105).Select(i => Build.Type($"App.N{i:D3}.Type"))
            .Concat(Enumerable.Range(0, 105).Select(i => Build.Type($"App.T{i:D3}"))));
        var seen = new HashSet<string>();
        for (var page = 0; page < 11; page++)
        {
            var view = View(model, page: page);
            Assert.Equal(210, view.Page!.Total);
            Assert.Equal(page, view.Page.Index);
            Assert.InRange(view.Nodes.Count, 1, ViewBuilder.PageSize + 1);
            Assert.Equal(210, view.Nodes.Sum(n => n.TypeCount));
            foreach (var node in view.Nodes.Where(n => n.Kind != ViewNodeKind.Summary))
            {
                Assert.True(seen.Add(node.Id));
                if (node.Drill is { } drill)
                    Assert.Single(View(model, focus: drill).Nodes, n => n.TypeId is not null);
                else Assert.NotNull(TypeCardBuilder.Build(model, new Policy { Prefix = "App" }, MetricsSet.Empty,
                    TreeBuilder.Real(model, new Policy { Prefix = "App" }), node.TypeId!));
            }
        }
        Assert.Equal(210, seen.Count);
        Assert.Equal(10, View(model, page: int.MaxValue).Page!.Index);
        Assert.Equal(0, View(model, page: -1).Page!.Index);
    }

    [Fact]
    public void CollapsedEntriesRetainReferenceCountsAndTrueTypeLevelViolations()
    {
        var model = Build.Model(Enumerable.Range(0, 100).Select(i => Build.Type($"App.T{i:D3}")),
            [Build.Edge("App.T000", "App.T099", count: 7), Build.Edge("App.T050", "App.T099", count: 11),
                Build.Edge("App.T099", "App.T050", count: 3)]);
        var policy = Build.Policy(["T000", "T050"], ["T099"]);
        foreach (var page in new[] { 0, 2, 4 })
        {
            var view = View(model, policy, page: page);
            Assert.Equal(21, view.Nodes.Sum(n => n.InternalReferences) + view.Edges.Sum(e => e.Pairs.Sum(p => p.Count)));
            Assert.Equal(18, view.Nodes.Sum(n => n.InternalViolations) + view.Edges.Sum(e => e.Pairs.Where(p => p.Violating).Sum(p => p.Count)));
        }
        var other = View(model, policy).Nodes.Single(n => n.Kind == ViewNodeKind.Summary);
        Assert.Equal(14, other.InternalReferences);
        Assert.Equal(11, other.InternalViolations);
        Assert.Equal(1, other.Level);
        Assert.Equal("Other entries on this level", other.Label);
        Assert.Null(other.Target);
        Assert.Null(other.Drill);
    }

    [Fact]
    public void ANameFilterKeepsOtherEntriesInTheGraphAndCanFindTheLastGlobalType()
    {
        var model = Build.Model(Enumerable.Range(0, 100).Select(i => Build.Type($"App.T{i:D3}")),
            [Build.Edge("App.T000", "App.T099", count: 5)]);
        var view = View(model, page: 100, query: " t099 ");
        Assert.Equal(new ViewPage(0, ViewBuilder.PageSize, 100, 1, "t099"), view.Page);
        Assert.Equal("App.T099", view.Nodes.Single(n => n.TypeId is not null).TypeId);
        Assert.Equal(5, Assert.Single(Assert.Single(view.Edges).Pairs).Count);
        var empty = View(model, query: "absent");
        Assert.Equal(0, empty.Page!.Matches);
        Assert.Equal(100, Assert.Single(empty.Nodes).TypeCount);
        Assert.Equal(5, empty.Nodes[0].InternalReferences);
    }

    [Fact]
    public void ADeepWideNamespaceOpensAsAComponentBeforeItsPagedContents()
    {
        var model = Build.Model(Enumerable.Range(0, 100).Select(i => Build.Type($"App.Deep.T{i:D3}")));
        var overview = View(model);
        var component = Assert.Single(overview.Nodes);
        Assert.Equal("ns:Deep", component.Drill);
        Assert.Equal(100, component.TypeCount);
        Assert.Equal(1, overview.Page!.Total);
        Assert.Equal(100, View(model, focus: component.Drill).Page!.Total);
    }

    [Fact]
    public void LargeExternalFanoutIsBoundedWithoutDroppingPairsOrLibraries()
    {
        var globals = Enumerable.Range(0, 100).Select(i => Build.Type($"App.T{i:D3}")).ToList();
        var libraries = Enumerable.Range(0, 30).Select(i => new ForeignNode($"x:Library{i}", $"Library{i}")).ToList();
        var model = Build.Model([Build.Type("App.Focus.Type"), .. globals],
            globals.Select(t => Build.Edge(t.Id, "App.Focus.Type", count: 3))
                .Concat(libraries.Select(f => Build.Edge("App.Focus.Type", f.Id, count: 2)))
                .Append(Build.Edge("App.T000", "App.T001", count: 1000)), libraries);
        var view = View(model, focus: "ns:Focus");
        Assert.InRange(view.Nodes.Count, 1, ViewBuilder.ExteriorLimit + 1);
        Assert.Equal(130, view.Edges.Sum(e => e.Pairs.Count));
        Assert.Equal(360, view.Edges.Sum(e => e.Pairs.Sum(p => p.Count)));
        Assert.Equal(89, view.Nodes.Single(n => n.Kind == ViewNodeKind.Summary).TypeCount);
        Assert.Equal("Other outside dependencies", view.Nodes.Single(n => n.Kind == ViewNodeKind.Summary).Label);
        Assert.All(view.Edges, e =>
        {
            Assert.Contains(view.Nodes, n => n.Id == e.From);
            Assert.Contains(view.Nodes, n => n.Id == e.To);
        });
    }

    [Fact]
    public void EqualTypeNamesHaveDeterministicPageMembership()
    {
        var types = Enumerable.Range(0, 100).Select(i => Build.Type($"App.T{i:D3}") with { Name = "Duplicate" }).ToList();
        Assert.Equal(View(Build.Model(types)).Nodes.Select(n => n.Id), View(Build.Model(types.AsEnumerable().Reverse())).Nodes.Select(n => n.Id));
        Assert.Equal("App.T000", View(Build.Model(types)).Nodes[0].TypeId);
    }

    [Fact]
    public void FilteringAlsoWorksOnSmallViewsAndKeepsTheWholeBreadcrumbPath()
    {
        var model = Build.Model([Build.Type("App.One.Two.A"), Build.Type("App.One.Two.B")]);
        var view = View(model, focus: "ns:One.Two", query: "b");
        Assert.Equal(new ViewPage(0, ViewBuilder.PageSize, 2, 1, "b"), view.Page);
        Assert.Equal("App.One.Two.B", view.Nodes[0].TypeId);
        Assert.Equal(["ns:", "ns:One", "ns:One.Two"], view.Breadcrumbs.Select(b => b.Id));
    }

    [Fact]
    public void ViewsAtTheBoundsStayExpandedWithoutAnUnnecessarySummary()
    {
        var types = Enumerable.Range(0, 80).Select(i => Build.Type($"App.Focus.T{i:D3}")).ToList();
        var outside = Enumerable.Range(0, 12).Select(i => Build.Type($"App.Other{i:D2}.Type")).ToList();
        var model = Build.Model([.. types, .. outside], outside.Select(t => Build.Edge(types[0].Id, t.Id)));
        var view = View(model, focus: "ns:Focus");
        Assert.Null(view.Page);
        Assert.Equal(92, view.Nodes.Count);
        Assert.DoesNotContain(view.Nodes, n => n.Kind == ViewNodeKind.Summary);
        Assert.NotNull(View(model with { Types = [.. model.Types, Build.Type("App.Focus.Extra")] }, focus: "ns:Focus").Page);
    }

    [Fact]
    public void UnrelatedExteriorEdgesAndInteriorEdgesCannotConsumeTheExteriorBudget()
    {
        var types = Enumerable.Range(0, 15).Select(i => Build.Type($"App.Focus.T{i:D2}")).ToList();
        var outside = Enumerable.Range(0, 15).Select(i => Build.Type($"App.Other{i:D2}.Type")).ToList();
        var edges = types.Skip(1).Select(t => Build.Edge(types[0].Id, t.Id))
            .Concat(outside.Skip(1).Select(t => Build.Edge(outside[0].Id, t.Id)));
        var view = View(Build.Model([.. types, .. outside], edges), focus: "ns:Focus");
        Assert.Equal(15, view.Nodes.Count);
        Assert.Equal(14, view.Edges.Count);
        Assert.All(view.Nodes, n => Assert.Equal(ViewNodeKind.Type, n.Kind));
    }

    [Fact]
    public void MixedBundlesKeepAnyViolationAndSortTheirPairsAndEndpoints()
    {
        var model = Build.Model([Build.Type("App.Left.A"), Build.Type("App.Left.B"), Build.Type("App.Left.C"),
            Build.Type("App.Right.A"), Build.Type("App.Right.B"), Build.Type("App.Right.C"),
            .. Enumerable.Range(0, 80).Select(i => Build.Type($"App.Other.T{i:D2}"))],
            [Build.Edge("App.Right.A", "App.Left.A"), Build.Edge("App.Left.B", "App.Other.T01"),
                Build.Edge("App.Left.B", "App.Right.B"), Build.Edge("App.Left.B", "App.Right.A"), Build.Edge("App.Left.A", "App.Right.A")]);
        var view = View(model, Build.Policy(["Left.A"], ["Left.B", "Right.A", "Right.B"]));
        Assert.Equal([("ns:Left", "ns:Other"), ("ns:Left", "ns:Right"), ("ns:Right", "ns:Left")], view.Edges.Select(e => (e.From, e.To)));
        var bundle = view.Edges[1];
        Assert.True(bundle.Violating);
        Assert.Equal([("Left.A", "Right.A"), ("Left.B", "Right.A"), ("Left.B", "Right.B")], bundle.Pairs.Select(p => (p.From, p.To)));
        Assert.Single(bundle.Pairs, p => p.Violating);
    }

    [Fact]
    public void HiddenRiskGradesAndPolicyEdgeOverridesArePreserved()
    {
        var bad = Build.Type("App.T099", Build.Member("App.T099", "Risk", 12));
        var model = Build.Model([.. Enumerable.Range(0, 99).Select(i => Build.Type($"App.T{i:D3}")), bad],
            [Build.Edge("App.T050", "App.T099", count: 7), Build.Edge("App.T051", "App.T099", count: 11),
                Build.Edge("App.T052", "App.T099", count: 13)]);
        var policy = Build.Policy(["T050", "T051", "T052"], ["T099"]) with
        {
            Omit = ["T052"],
            OmitEdges = [new EdgeRule { From = "T051", To = "T099" }],
            EdgeKinds = [new EdgeRule { From = "T050", To = "T099", Kind = EdgeKind.Association }],
        };
        var metrics = new MetricsSet(new CrapSnapshot(DateTimeOffset.UnixEpoch, new Dictionary<string, CrapEntry>
        {
            [bad.Members[0].Id] = new(12, 0, 156, 1, 0, "h"),
        }), MutationSnapshot.Empty);
        var view = ViewBuilder.Build(model, policy, metrics, TreeBuilder.Real(model, policy), null);
        var other = view.Nodes.Single(n => n.Kind == ViewNodeKind.Summary);
        Assert.Equal(79, other.TypeCount);
        Assert.Equal(1, other.Grades.Crap);
        Assert.Equal(7, other.InternalReferences);
        Assert.Equal(0, other.InternalViolations);
        Assert.Empty(view.Edges);
    }

    [Fact]
    public void ProposalsUseTheSamePagesWithoutChangingLayerAssignments()
    {
        var model = Build.Model(Enumerable.Range(0, 100).Select(i => Build.Type($"App.T{i:D3}")));
        var policy = new Policy { Prefix = "App" };
        var proposal = new Proposal { Id = "trial", Name = "Trial" };
        var tree = TreeBuilder.ForProposal(model, policy, proposal);
        var view = ViewBuilder.Build(model, policy, MetricsSet.Empty, tree, "g:unassigned", 4);
        Assert.True(view.Context.IsProposal);
        Assert.Equal(100, view.Page!.Total);
        Assert.Equal("App.T080", view.Nodes.First().TypeId);
    }
}
