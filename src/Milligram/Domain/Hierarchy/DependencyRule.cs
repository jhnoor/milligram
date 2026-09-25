using Milligram.Domain.Model;
using Milligram.Domain.Policies;

namespace Milligram.Domain.Hierarchy;

/// <summary>Clean Architecture's dependency rule: source dependencies point inward (to lower levels).</summary>
public static class DependencyRule
{
    public static bool IsViolating(EdgeKind kind, int? fromLevel, int? toLevel) =>
        kind != EdgeKind.Association && fromLevel is { } from && toLevel is { } to && from < to;

    /// <summary>Level of a relative type name: the index of the group holding its longest covering path.</summary>
    public static Func<string, int?> LevelsFrom(IReadOnlyList<IReadOnlyList<string>> levels)
    {
        var ranked = levels
            .SelectMany((group, index) => group.Select(path => (path, index)))
            .ToList();
        return name =>
        {
            int? level = null;
            var bestLength = -1;
            foreach (var (path, index) in ranked)
            {
                if (path.Length <= bestLength || !NamePath.Covers(path, name)) continue;
                bestLength = path.Length;
                level = index;
            }
            return level;
        };
    }
}

/// <summary>Applies the policy's omit-edges and edge-kind overrides to scanned edges.</summary>
public static class EdgeRules
{
    public static IReadOnlyList<DependencyEdge> Apply(IEnumerable<DependencyEdge> edges, Policy policy, Func<string, string> nameOf) =>
        edges
            .Where(e => !policy.OmitEdges.Any(r => r.Matches(nameOf(e.From), nameOf(e.To))))
            .Select(e => Override(e, policy, nameOf))
            .ToList();

    private static DependencyEdge Override(DependencyEdge edge, Policy policy, Func<string, string> nameOf)
    {
        var rule = policy.EdgeKinds.LastOrDefault(r => r.Kind is not null && r.Matches(nameOf(edge.From), nameOf(edge.To)));
        return rule is null ? edge : edge with { Kind = rule.Kind!.Value };
    }
}
