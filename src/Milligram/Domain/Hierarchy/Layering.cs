using Milligram.Domain.Model;
using Milligram.Domain.Policies;

namespace Milligram.Domain.Hierarchy;

/// <summary>Proposed levels, inner (0) first, and the dependencies they leave pointing outward.</summary>
public sealed record Layers(IReadOnlyList<IReadOnlyList<string>> Levels, IReadOnlyList<DependencyEdge> Outward);

/// <summary>
/// Proposes levels from the dependencies alone: what depends on nothing is level 0, and everything else sits
/// one above the highest thing it depends on. Cycles are broken at their lightest dependencies, which are
/// left pointing outward (drawn red), so a first diagram shows where the tangles are.
/// </summary>
public static class Layering
{
    /// <summary>Levels for the top-level namespaces under <paramref name="prefix"/>, weighted by reference counts.</summary>
    public static Layers TopLevel(CodeModel model, string prefix)
    {
        var segmentOf = model.Types
            .Select(t => (t.Id, Segment: NamePath.Segments(NamePath.Relative(t.Namespace, prefix)).FirstOrDefault()))
            .Where(t => t.Segment is not null)
            .ToDictionary(t => t.Id, t => t.Segment!, StringComparer.Ordinal);
        var edges = model.Edges
            .Where(e => segmentOf.ContainsKey(e.From) && segmentOf.ContainsKey(e.To))
            .Select(e => e with { From = segmentOf[e.From], To = segmentOf[e.To] });
        return Infer(segmentOf.Values, edges);
    }

    public static Layers Infer(IEnumerable<string> nodes, IEnumerable<DependencyEdge> edges)
    {
        var names = nodes.Distinct().Order(StringComparer.Ordinal).ToList();
        var known = names.ToHashSet(StringComparer.Ordinal);
        var weights = edges
            .Where(e => e.Kind != EdgeKind.Association && e.From != e.To && known.Contains(e.From) && known.Contains(e.To))
            .GroupBy(e => (e.From, e.To))
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Count));

        var order = InnerFirst(names, weights);
        var position = order.Select((name, index) => (name, index)).ToDictionary(x => x.name, x => x.index, StringComparer.Ordinal);
        var level = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in order)
            level[name] = weights.Keys
                .Where(k => k.From == name && position[k.To] < position[name])
                .Select(k => level[k.To] + 1)
                .DefaultIfEmpty(0)
                .Max();

        var levels = order
            .GroupBy(name => level[name])
            .OrderBy(g => g.Key)
            .Select(g => (IReadOnlyList<string>)g.Order(StringComparer.Ordinal).ToList())
            .ToList();
        var outward = weights
            .Where(w => DependencyRule.IsViolating(EdgeKind.Dependency, level[w.Key.From], level[w.Key.To]))
            .Select(w => new DependencyEdge(w.Key.From, w.Key.To, EdgeKind.Dependency, w.Value))
            .OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal)
            .ToList();
        return new Layers(levels, outward);
    }

    /// <summary>
    /// A linear order, innermost first, that tries to keep dependencies pointing inward. Sinks go inside and
    /// sources outside, so an acyclic graph keeps every edge. In a cycle, the node that depends least compared
    /// with how much it is depended on goes inside, and its remaining dependencies become the outward ones.
    /// Ties go to the ordinal first name, so the result is deterministic.
    /// </summary>
    private static List<string> InnerFirst(List<string> names, Dictionary<(string From, string To), int> weights)
    {
        var outgoing = weights.ToLookup(w => w.Key.From, w => (Node: w.Key.To, Weight: w.Value));
        var incoming = weights.ToLookup(w => w.Key.To, w => (Node: w.Key.From, Weight: w.Value));
        var remaining = names.ToHashSet(StringComparer.Ordinal);
        int Out(string name) => outgoing[name].Where(e => remaining.Contains(e.Node)).Sum(e => e.Weight);
        int In(string name) => incoming[name].Where(e => remaining.Contains(e.Node)).Sum(e => e.Weight);

        var inner = new List<string>();
        var outer = new List<string>();
        while (remaining.Count > 0)
        {
            var candidates = names.Where(remaining.Contains).ToList();
            var sink = candidates.FirstOrDefault(n => Out(n) == 0);
            var source = sink is null ? candidates.FirstOrDefault(n => In(n) == 0) : null;
            var next = sink ?? source ?? candidates.MinBy(n => Out(n) - In(n))!;
            (source is not null ? outer : inner).Add(next);
            remaining.Remove(next);
        }
        outer.Reverse();
        return [.. inner, .. outer];
    }
}
