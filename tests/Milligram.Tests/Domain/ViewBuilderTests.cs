using Milligram.Domain.Hierarchy;
using Milligram.Domain.Metrics;
using Milligram.Domain.Model;
using Milligram.Domain.Policies;
using Milligram.Domain.Views;

namespace Milligram.Tests.Domain;

public class ViewBuilderTests
{
    private static DiagramView View(string? focus, Policy? policy = null, CodeModel? model = null)
    {
        model ??= Build.LayeredApp();
        policy ??= Build.LayeredPolicy();
        return ViewBuilder.Build(model, policy, MetricsSet.Empty, TreeBuilder.Real(model, policy), focus);
    }

    [Fact]
    public void TheTopLevelShowsComponentsWithOneLevelOfContents()
    {
        var view = View(null);

        Assert.Equal("ns:", view.Focus.Id);
        var components = view.Nodes.Where(n => n.Kind == ViewNodeKind.Component).Select(n => n.Id);
        Assert.Equal(["ns:Domain", "ns:Services", "ns:Web"], components);
        var rules = view.Nodes.Single(n => n.Id == "ns:Domain.Rules");
        Assert.Equal(ViewNodeKind.Package, rules.Kind);
        Assert.Equal("ns:Domain", rules.Parent);
        var program = view.Nodes.Single(n => n.Label == "Program");
        Assert.Equal(ViewNodeKind.Type, program.Kind);
        Assert.Null(program.Parent);
    }

    [Fact]
    public void EdgesBundleToTheVisibleBoxesAndKeepTheirPairs()
    {
        var view = View(null);

        var checkoutToRules = view.Edges.Single(e => e.From == "t:App.Services.Checkout" && e.To == "ns:Domain.Rules");
        Assert.Equal("Services.Checkout", Assert.Single(checkoutToRules.Pairs).From);
        Assert.Equal("Domain.Rules.Pricing", checkoutToRules.Pairs[0].To);
    }

    [Fact]
    public void ViolationsAreMarkedFromLevels()
    {
        var view = View(null);

        var violation = view.Edges.Single(e => e.From == "t:App.Domain.Order" && e.To == "t:App.Web.Controller");
        Assert.True(violation.Violating);
        Assert.All(view.Edges.Where(e => e != violation), e => Assert.False(e.Violating));
    }

    [Fact]
    public void ImplementsKeepsItsKind()
    {
        var view = View(null);
        var implements = view.Edges.Single(e => e.From == "ns:Web.Sql");
        Assert.Equal(EdgeKind.Implements, implements.Kind);
    }

    [Fact]
    public void DrillingInShowsTheOutsideAsExternalBoxes()
    {
        var view = View("ns:Services");

        Assert.Equal(["ns:", "ns:Services"], view.Breadcrumbs.Select(b => b.Id));
        Assert.Contains(view.Nodes, n => n.Id == "t:App.Services.Checkout" && n.Kind == ViewNodeKind.Type && n.Parent is null);
        var externals = view.Nodes.Where(n => n.Kind == ViewNodeKind.External).Select(n => n.Id).ToList();
        Assert.Contains("ns:Domain", externals);
        Assert.Contains("ns:Web", externals);
        Assert.DoesNotContain("t:App.Program", externals);
    }

    [Fact]
    public void EdgesBetweenTwoOutsideBoxesAreNotShown()
    {
        var view = View("ns:Services");
        Assert.DoesNotContain(view.Edges, e => e.From == "ns:Domain" && e.To == "ns:Web");
    }

    [Fact]
    public void ForeignLibrariesAppearWhenReferenced()
    {
        var view = View("ns:Web");
        var foreign = view.Nodes.Single(n => n.Kind == ViewNodeKind.Foreign);
        Assert.Equal("Microsoft.AspNetCore", foreign.Label);
        Assert.Contains(view.Edges, e => e.From == "t:App.Web.Controller" && e.To == foreign.Id);
    }

    [Fact]
    public void MaxLevelIsTheOutermostVisibleLevel() => Assert.Equal(2, View(null).MaxLevel);

    [Fact]
    public void BoxMembersSkipPrivateAndNestedMembers()
    {
        var type = Build.Type("App.X",
            Build.Member("App.X", "Visible"),
            Build.Member("App.X", "Hidden", visibility: Visibility.Private),
            Build.Member("App.X", "Inner.Nested"));
        Assert.Equal(["Visible"], ViewBuilder.BoxMembers(type).Select(m => m.Name));
    }

    [Theory]
    [InlineData(TypeKind.Interface, false, false, "interface")]
    [InlineData(TypeKind.Class, true, false, "abstract")]
    [InlineData(TypeKind.Class, false, true, "static")]
    [InlineData(TypeKind.Record, false, false, "record")]
    [InlineData(TypeKind.Enum, false, false, "enum")]
    [InlineData(TypeKind.Class, false, false, "class")]
    public void StereotypesFollowTheKind(TypeKind kind, bool isAbstract, bool isStatic, string expected)
    {
        var type = Build.Type("App.X", kind) with { IsAbstract = isAbstract, IsStatic = isStatic };
        Assert.Equal(expected, ViewBuilder.StereotypeOf(type));
    }

    [Fact]
    public void ContainersTakeTheWorstGradeOfTheirTypes()
    {
        var good = Build.Type("App.Domain.Good", Build.Member("App.Domain.Good", "A", 1, "good.cs"));
        var bad = Build.Type("App.Domain.Bad", Build.Member("App.Domain.Bad", "B", 12, "bad.cs"));
        var model = Build.Model([good, bad]);
        var metrics = new MetricsSet(new CrapSnapshot(DateTimeOffset.UnixEpoch, new Dictionary<string, CrapEntry>
        {
            [good.Members[0].Id] = new(1, 1, 1, 1, 1, "h"),
            [bad.Members[0].Id] = new(12, 0, 156, 5, 0, "h"),
        }), MutationSnapshot.Empty);
        var policy = new Policy { Prefix = "App" };

        var view = ViewBuilder.Build(model, policy, metrics, TreeBuilder.Real(model, policy), null);

        Assert.Equal(1, view.Nodes.Single(n => n.Id == "ns:Domain").Grades.Crap);
        Assert.Equal(10, view.Nodes.Single(n => n.Label == "Good").Grades.Crap);
        Assert.Null(view.Nodes.Single(n => n.Label == "Good").Grades.Mutation);
    }
}

public class TypeCardBuilderTests
{
    [Fact]
    public void TheCardListsMembersAndDependenciesBothWays()
    {
        var order = Build.Type("App.Domain.Order", Build.Member("App.Domain.Order", "Total", 3, "order.cs", 5, 9));
        var model = Build.LayeredApp() with { Types = [.. Build.LayeredApp().Types.Where(t => t.Id != order.Id), order] };
        var policy = Build.LayeredPolicy();

        var card = TypeCardBuilder.Build(model, policy, MetricsSet.Empty, TreeBuilder.Real(model, policy), order.Id)!;

        Assert.Equal("Domain.Order", card.RelativeName);
        Assert.Equal(0, card.Level);
        Assert.Equal(3, Assert.Single(card.Members).Complexity);
        var dependsOn = Assert.Single(card.DependsOn);
        Assert.Equal("Web.Controller", dependsOn.Label);
        Assert.True(dependsOn.Violating);
        Assert.Equal("Services.Checkout", Assert.Single(card.UsedBy).Label);
        Assert.Equal(3, card.UsedBy[0].Count);
    }

    [Fact]
    public void UnknownTypesHaveNoCard()
    {
        var model = Build.LayeredApp();
        Assert.Null(TypeCardBuilder.Build(model, Build.LayeredPolicy(), MetricsSet.Empty, TreeBuilder.Real(model, Build.LayeredPolicy()), "App.Nope"));
    }

    [Fact]
    public void StaleMetricsAreFlagged()
    {
        var type = Build.Type("App.A", Build.Member("App.A", "M", 2, hash: "new"));
        var model = Build.Model([type]);
        var metrics = new MetricsSet(
            new CrapSnapshot(DateTimeOffset.UnixEpoch, new Dictionary<string, CrapEntry> { [type.Members[0].Id] = new(2, 1, 2, 1, 1, "old") }),
            MutationSnapshot.Empty);
        var policy = new Policy { Prefix = "App" };

        var card = TypeCardBuilder.Build(model, policy, metrics, TreeBuilder.Real(model, policy), type.Id)!;

        Assert.True(card.Members[0].CrapStale);
        Assert.True(card.Crap!.Stale);
    }
}
