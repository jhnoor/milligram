using Milligram.Domain.Hierarchy;
using Milligram.Domain.Model;
using Milligram.Domain.Policies;

namespace Milligram.Tests.Domain;

public class TreeBuilderTests
{
    [Fact]
    public void TheRealTreeIsTheNamespaceTree()
    {
        var tree = TreeBuilder.Real(Build.LayeredApp(), Build.LayeredPolicy());

        Assert.Equal(["Domain", "Services", "Web"], tree.Root.Children.Select(c => c.Label));
        Assert.Equal(["Program"], tree.Root.Types.Select(t => t.Name));
        var domain = tree.Find("ns:Domain");
        Assert.Equal(["Order"], domain.Types.Select(t => t.Name));
        Assert.Equal("ns:Domain.Rules", Assert.Single(domain.Children).Id);
        Assert.Equal("Domain.Rules", domain.Children[0].Path);
    }

    [Fact]
    public void OrderSortsTopLevelComponents()
    {
        var policy = Build.LayeredPolicy() with { Order = ["Web", "Domain"] };
        var tree = TreeBuilder.Real(Build.LayeredApp(), policy);
        Assert.Equal(["Web", "Domain", "Services"], tree.Root.Children.Select(c => c.Label));
    }

    [Fact]
    public void OmittedNamespacesAndTypesAreLeftOut()
    {
        var policy = Build.LayeredPolicy() with { Omit = ["Web.Sql", "Domain.Order"] };
        var tree = TreeBuilder.Real(Build.LayeredApp(), policy);
        Assert.False(tree.Contains("App.Web.Sql.SqlRepository"));
        Assert.False(tree.Contains("App.Domain.Order"));
        Assert.True(tree.Contains("App.Web.Controller"));
    }

    [Fact]
    public void LevelsComeFromThePolicyAndComponentsTakeTheirMaximum()
    {
        var policy = Build.Policy(["Domain"], ["Services", "Domain.Rules"]);
        var tree = TreeBuilder.Real(Build.LayeredApp(), policy);
        Assert.Equal(0, tree.LevelOfType("App.Domain.Order"));
        Assert.Equal(1, tree.LevelOfType("App.Domain.Rules.Pricing"));
        Assert.Equal(1, tree.LevelOf(tree.Find("ns:Domain")));
        Assert.Null(tree.LevelOfType("App.Web.Controller"));
    }

    [Fact]
    public void AProposalRegroupsNamespacesIntoNamedLayers()
    {
        var proposal = new Proposal
        {
            Id = "p1",
            Name = "Core split",
            Layers =
            [
                new ProposalGroup { Id = "core", Label = "Core", Namespaces = [ProposalEntry.Of("Domain"), ProposalEntry.Of("Services.IRepository")] },
                new ProposalGroup
                {
                    Id = "outer", Label = "Outer",
                    Namespaces = [ProposalEntry.Of(new ProposalGroup { Id = "io", Label = "IO", Namespaces = [ProposalEntry.Of("Web.Sql")] })],
                },
            ],
            Omit = ["Web.Controller"],
        };
        var policy = Build.LayeredPolicy() with { Proposals = [proposal] };

        var tree = TreeBuilder.ForContext(Build.LayeredApp(), policy, "p1");

        Assert.True(tree.IsProposal);
        Assert.Equal(["Core", "Outer", "Unassigned"], tree.Root.Children.Select(c => c.Label));
        var core = tree.Find("g:core");
        Assert.Equal(["IRepository"], core.Types.Select(t => t.Name));
        Assert.Equal("ns:Domain", Assert.Single(core.Children).Id);
        Assert.Equal("ns:Domain.Rules", Assert.Single(tree.Find("ns:Domain").Children).Id);
        Assert.Equal("ns:Web.Sql", Assert.Single(tree.Find("g:io").Children).Id);
        Assert.False(tree.Contains("App.Web.Controller"));
        Assert.Equal(0, tree.LevelOfType("App.Domain.Rules.Pricing"));
        Assert.Equal(1, tree.LevelOfType("App.Web.Sql.SqlRepository"));
        Assert.Null(tree.LevelOfType("App.Services.Checkout"));
        Assert.Contains(tree.Find(TreeBuilder.UnassignedId).AllTypes(), t => t.Name == "Checkout");
    }

    [Fact]
    public void AnUnknownContextFallsBackToTheRealTree()
    {
        var tree = TreeBuilder.ForContext(Build.LayeredApp(), Build.LayeredPolicy(), "nope");
        Assert.False(tree.IsProposal);
        Assert.Equal(DiagramTree.RealContext, tree.ContextId);
    }

    [Fact]
    public void WithoutLevelsTheFirstProposalRanksTheRealDiagram()
    {
        var proposal = new Proposal
        {
            Id = "p1",
            Layers = [new ProposalGroup { Id = "in", Namespaces = [ProposalEntry.Of("Domain")] }, new ProposalGroup { Id = "out", Namespaces = [ProposalEntry.Of("Web")] }],
        };
        var tree = TreeBuilder.Real(Build.LayeredApp(), new Policy { Prefix = "App", Proposals = [proposal] });
        Assert.Equal(0, tree.LevelOfType("App.Domain.Order"));
        Assert.Equal(1, tree.LevelOfType("App.Web.Controller"));
    }

    [Fact]
    public void FindReturnsTheRootForUnknownIds()
    {
        var tree = TreeBuilder.Real(Build.LayeredApp(), Build.LayeredPolicy());
        Assert.Same(tree.Root, tree.Find("ns:Missing"));
        Assert.Same(tree.Root, tree.Find(null));
        Assert.Null(tree.TryFind("ns:Missing"));
    }

    [Fact]
    public void GlobalNamespaceTypesSitAtTheRoot()
    {
        var model = Build.Model([Build.Type("Program"), Build.Type("App.Domain.Order")]);
        var tree = TreeBuilder.Real(model, new Policy { Prefix = "App" });
        Assert.Contains(tree.Root.Types, t => t.Name == "Program");
        Assert.Equal(TypeKind.Class, tree.Root.Types[0].Kind);
    }
}
