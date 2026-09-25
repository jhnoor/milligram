using Milligram.Domain.Hierarchy;
using Milligram.Domain.Model;
using static Milligram.Tests.Build;

namespace Milligram.Tests.Domain;

public class LayeringTests
{
    private static IReadOnlyList<IReadOnlyList<string>> Levels(params string[][] levels) => levels;

    private static string Arrows(Layers layers) => string.Join(" ", layers.Outward.Select(e => $"{e.From}->{e.To}:{e.Count}"));

    [Fact]
    public void AChainIsLayeredInnerFirstWithNothingOutward()
    {
        var layers = Layering.Infer(["Web", "Services", "Domain"], [Edge("Web", "Services"), Edge("Services", "Domain")]);

        Assert.Equal(Levels(["Domain"], ["Services"], ["Web"]), layers.Levels);
        Assert.Empty(layers.Outward);
    }

    [Fact]
    public void ANamespaceSitsOneAboveTheHighestThingItDependsOn()
    {
        var layers = Layering.Infer(
            ["Main", "Web", "Sql", "Services", "Domain"],
            [Edge("Main", "Web"), Edge("Main", "Domain"), Edge("Web", "Services"), Edge("Sql", "Services"), Edge("Services", "Domain")]);

        Assert.Equal(Levels(["Domain"], ["Services"], ["Sql", "Web"], ["Main"]), layers.Levels);
        Assert.Empty(layers.Outward);
    }

    [Fact]
    public void AnAcyclicGraphGetsNoOutwardDependenciesWhateverTheWeights()
    {
        var layers = Layering.Infer(["X", "Y", "Z"], [Edge("X", "Y", count: 50), Edge("Y", "Z")]);

        Assert.Equal(Levels(["Z"], ["Y"], ["X"]), layers.Levels);
        Assert.Empty(layers.Outward);
    }

    [Fact]
    public void NamespacesWithNoDependenciesAreInnermost()
    {
        var layers = Layering.Infer(["Web", "Domain", "Util"], [Edge("Web", "Domain")]);

        Assert.Equal(Levels(["Domain", "Util"], ["Web"]), layers.Levels);
    }

    [Fact]
    public void ACycleIsBrokenAtItsLightestDependency()
    {
        var layers = Layering.Infer(["Domain", "Web"], [Edge("Web", "Domain", count: 5), Edge("Domain", "Web", count: 1)]);

        Assert.Equal(Levels(["Domain"], ["Web"]), layers.Levels);
        Assert.Equal("Domain->Web:1", Arrows(layers));
    }

    [Fact]
    public void ALongerCycleLeavesOnlyItsWeakLinkOutward()
    {
        var layers = Layering.Infer(["A", "B", "C"], [Edge("A", "B", count: 5), Edge("B", "C", count: 5), Edge("C", "A", count: 1)]);

        Assert.Equal(Levels(["C"], ["B"], ["A"]), layers.Levels);
        Assert.Equal("C->A:1", Arrows(layers));
    }

    [Fact]
    public void AnEvenCycleIsBrokenDeterministically()
    {
        var layers = Layering.Infer(["B", "A"], [Edge("A", "B"), Edge("B", "A")]);

        Assert.Equal(Levels(["A"], ["B"]), layers.Levels);
        Assert.Equal("A->B:1", Arrows(layers));
    }

    [Fact]
    public void ReferencesAreSummedAcrossTypes()
    {
        var layers = Layering.Infer(
            ["Domain", "Web"],
            [Edge("Domain", "Web"), Edge("Domain", "Web"), Edge("Domain", "Web"), Edge("Web", "Domain", count: 2)]);

        Assert.Equal(Levels(["Web"], ["Domain"]), layers.Levels);
        Assert.Equal("Web->Domain:2", Arrows(layers));
    }

    [Fact]
    public void ANamespaceThatOnlyUsesACycleDoesNotTipIt()
    {
        var layers = Layering.Infer(
            ["A", "B", "Tool"],
            [Edge("A", "B", count: 10), Edge("B", "A", count: 10), Edge("Tool", "B")]);

        Assert.Equal(Levels(["A"], ["B"], ["Tool"]), layers.Levels);
        Assert.Equal("A->B:10", Arrows(layers));
    }

    [Fact]
    public void AChainHangingOffACycleKeepsItsLayers()
    {
        var layers = Layering.Infer(
            ["A", "B", "X", "Y"],
            [Edge("A", "B", count: 2), Edge("B", "A"), Edge("Y", "A"), Edge("X", "Y")]);

        Assert.Equal(Levels(["B"], ["A"], ["Y"], ["X"]), layers.Levels);
        Assert.Equal("B->A:1", Arrows(layers));
    }

    [Fact]
    public void OutwardDependenciesAreListedInNameOrder()
    {
        var layers = Layering.Infer(
            ["A", "B", "C", "D", "E"],
            [Edge("D", "C", count: 3), Edge("C", "D"), Edge("E", "A", count: 3), Edge("A", "E"), Edge("B", "A", count: 3), Edge("A", "B")]);

        Assert.Equal("A->B:1 A->E:1 C->D:1", Arrows(layers));
    }

    [Fact]
    public void AssociationsAndUnknownNamespacesDoNotShapeTheLevels()
    {
        var layers = Layering.Infer(
            ["Domain", "Web"],
            [Edge("Web", "Domain"), Edge("Domain", "Web", EdgeKind.Association), Edge("Web", "Elsewhere")]);

        Assert.Equal(Levels(["Domain"], ["Web"]), layers.Levels);
        Assert.Empty(layers.Outward);
    }

    [Fact]
    public void TopLevelGroupsTypesByTheirFirstSegmentUnderThePrefix()
    {
        var layers = Layering.TopLevel(LayeredApp(), "App");

        // Web -> Services (2), Services -> Domain (4), and one Domain -> Web back edge; App.Program has no segment.
        Assert.Equal(Levels(["Domain"], ["Services"], ["Web"]), layers.Levels);
        Assert.Equal("Domain->Web:1", Arrows(layers));
    }
}
