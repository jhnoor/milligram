using Milligram.Analysis.CSharp;
using Milligram.Application;
using Milligram.Domain.Hierarchy;
using Milligram.Domain.Policies;

namespace Milligram.Tests;

/// <summary>Milligram checks its own architecture with its own scanner and policy.</summary>
public class ArchitectureTests
{
    [Fact]
    public void MilligramObeysItsOwnDependencyRule()
    {
        var root = TempProject.RepositoryRoot();
        var policy = JsonFile.Read<Policy>(Path.Combine(root, "milligram.json"))!;
        var model = new CSharpScanner().Scan(new ScanRequest(
            root, Path.Combine(root, policy.Src), policy.Exclude, policy.Prefix, policy.Foreign, "Milligram"));
        var tree = TreeBuilder.Real(model, policy);

        var violations = EdgeRules.Apply(model.Edges, policy, id => id)
            .Where(e => DependencyRule.IsViolating(e.Kind, tree.LevelOfType(e.From), tree.LevelOfType(e.To)))
            .Select(e => $"{e.From} -> {e.To}")
            .ToList();

        Assert.True(model.Types.Count > 50);
        Assert.Empty(violations);
    }

    [Fact]
    public void TheDomainDependsOnNoFrameworks()
    {
        var root = TempProject.RepositoryRoot();
        var policy = JsonFile.Read<Policy>(Path.Combine(root, "milligram.json"))!;
        var model = new CSharpScanner().Scan(new ScanRequest(
            root, Path.Combine(root, policy.Src), policy.Exclude, policy.Prefix, ["Microsoft"], "Milligram"));

        var offenders = model.Edges
            .Where(e => e.From.StartsWith("Milligram.Domain.", StringComparison.Ordinal) && e.To.StartsWith("x:", StringComparison.Ordinal))
            .Select(e => $"{e.From} -> {e.To}")
            .ToList();

        Assert.Empty(offenders);
    }
}
