using Milligram.Domain.Hierarchy;
using Milligram.Domain.Model;
using Milligram.Domain.Policies;

namespace Milligram.Tests.Domain;

public class NamePathTests
{
    [Theory]
    [InlineData("App.Domain.Model", "App", "Domain.Model")]
    [InlineData("App", "App", "")]
    [InlineData("Other.Thing", "App", "Other.Thing")]
    [InlineData("AppX.Thing", "App", "AppX.Thing")]
    [InlineData("App.Domain", "", "App.Domain")]
    public void RelativeStripsThePrefixOnSegmentBoundaries(string ns, string prefix, string expected) =>
        Assert.Equal(expected, NamePath.Relative(ns, prefix));

    [Theory]
    [InlineData("", "Anything", true)]
    [InlineData("Domain", "Domain", true)]
    [InlineData("Domain", "Domain.Model.Order", true)]
    [InlineData("Domain", "DomainX", false)]
    [InlineData("Domain.Model", "Domain", false)]
    public void CoversMatchesWholeSegments(string path, string target, bool expected) =>
        Assert.Equal(expected, NamePath.Covers(path, target));

    [Fact]
    public void BareNameDropsTypeParameters() => Assert.Equal("Map", NamePath.BareName("Map<K, V>"));

    [Fact]
    public void LongestCoveringPrefersTheMostSpecificPath() =>
        Assert.Equal("Domain.Rules", NamePath.LongestCovering(["Domain", "Domain.Rules", "Web"], "Domain.Rules.Pricing"));

    [Fact]
    public void LongestCoveringReturnsNullWhenNothingCovers() =>
        Assert.Null(NamePath.LongestCovering(["Web"], "Domain.Order"));
}

public class GlobTests
{
    [Theory]
    [InlineData("**/bin/**", "src/App/bin/Debug/x.cs", true)]
    [InlineData("**/bin/**", "bin/x.cs", true)]
    [InlineData("**/bin/**", "src/binary/x.cs", false)]
    [InlineData("tests/**", "tests/App.Tests/A.cs", true)]
    [InlineData("tests/**", "src/tests.cs", false)]
    [InlineData("src/*.cs", "src/A.cs", true)]
    [InlineData("src/*.cs", "src/sub/A.cs", false)]
    [InlineData("src/?.cs", "src/A.cs", true)]
    public void MatchesRelativePaths(string glob, string path, bool expected) =>
        Assert.Equal(expected, Glob.Matches(glob, path));
}

public class DependencyRuleTests
{
    [Theory]
    [InlineData(EdgeKind.Dependency, 0, 1, true)]
    [InlineData(EdgeKind.Inheritance, 0, 2, true)]
    [InlineData(EdgeKind.Implements, 0, 1, true)]
    [InlineData(EdgeKind.Association, 0, 1, false)]
    [InlineData(EdgeKind.Dependency, 1, 0, false)]
    [InlineData(EdgeKind.Dependency, 1, 1, false)]
    public void InnerToOuterIsAViolation(EdgeKind kind, int from, int to, bool expected) =>
        Assert.Equal(expected, DependencyRule.IsViolating(kind, from, to));

    [Fact]
    public void UnrankedEndsAreNeverCompared()
    {
        Assert.False(DependencyRule.IsViolating(EdgeKind.Dependency, null, 1));
        Assert.False(DependencyRule.IsViolating(EdgeKind.Dependency, 0, null));
    }

    [Fact]
    public void LevelsUseTheLongestMatchingPath()
    {
        var levelOf = DependencyRule.LevelsFrom([["Domain"], ["Services", "Domain.Adapters"]]);
        Assert.Equal(0, levelOf("Domain.Order"));
        Assert.Equal(1, levelOf("Domain.Adapters.Sql"));
        Assert.Equal(1, levelOf("Services"));
        Assert.Null(levelOf("Web"));
    }

    [Fact]
    public void EdgeRulesOmitAndOverride()
    {
        var policy = new Policy
        {
            OmitEdges = [new EdgeRule { From = "A", To = "B" }],
            EdgeKinds = [new EdgeRule { From = "C", To = "D", Kind = EdgeKind.Association }],
        };
        var edges = EdgeRules.Apply(
            [Build.Edge("A.x", "B.y"), Build.Edge("C.x", "D.y"), Build.Edge("C.x", "E.y")], policy, id => id);
        Assert.Equal(2, edges.Count);
        Assert.Equal(EdgeKind.Association, edges.Single(e => e.To == "D.y").Kind);
        Assert.Equal(EdgeKind.Dependency, edges.Single(e => e.To == "E.y").Kind);
    }
}
