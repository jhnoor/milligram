using Milligram.Domain.Model;
using Milligram.Domain.Policies;

namespace Milligram.Domain.Hierarchy;

/// <summary>Builds the component tree: the namespace tree (real) or a proposal's named groups.</summary>
public static class TreeBuilder
{
    public const string UnassignedId = "g:unassigned";

    public static DiagramTree ForContext(CodeModel model, Policy policy, string? contextId)
    {
        var proposal = contextId is null ? null : policy.FindProposal(contextId);
        return proposal is null ? Real(model, policy) : ForProposal(model, policy, proposal);
    }

    public static DiagramTree Real(CodeModel model, Policy policy)
    {
        var root = new TreeNode("ns:", Title(model, policy), NodeKind.Root, null, "");
        var index = new Dictionary<string, TreeNode> { [root.Id] = root };
        var visible = Visible(model, policy, policy.Omit);
        foreach (var type in visible)
            Place(root, "", NamePath.Relative(type.Namespace, policy.Prefix), type, index);
        SortReal(root, policy.Order);
        return new DiagramTree(DiagramTree.RealContext, "Real diagram", root, RealLevels(visible, policy, model));
    }

    public static DiagramTree ForProposal(CodeModel model, Policy policy, Proposal proposal)
    {
        var root = new TreeNode("g:" + proposal.Id, proposal.Name, NodeKind.Root, null, null);
        var index = new Dictionary<string, TreeNode> { [root.Id] = root };
        var entries = new List<Assignment>();
        for (var level = 0; level < proposal.Layers.Count; level++)
            AddGroup(root, proposal.Layers[level], level, entries, index);

        var unassigned = new TreeNode(UnassignedId, "Unassigned", NodeKind.Group, root, null);
        index[unassigned.Id] = unassigned;
        var levels = new Dictionary<string, int?>();
        foreach (var type in Visible(model, policy, policy.Omit.Concat(proposal.Omit).ToList()))
            levels[type.Id] = Assign(type, policy.Prefix, entries, unassigned, index);

        if (!unassigned.IsEmpty) root.Children.Add(unassigned);
        SortAlphabetically(root, keepGroupOrder: true);
        return new DiagramTree(proposal.Id, proposal.Name, root, levels);
    }

    public static string RelativeName(TypeNode type, Policy policy) => NamePath.RelativeName(type, policy.Prefix);

    private sealed record Assignment(string Path, TreeNode Group, int Level);

    private static string Title(CodeModel model, Policy policy) =>
        policy.Title ?? (model.Title.Length > 0 ? model.Title : policy.Prefix);

    private static List<TypeNode> Visible(CodeModel model, Policy policy, IReadOnlyList<string> omit) =>
        model.Types
            .Where(t => !omit.Any(o => NamePath.Covers(o, RelativeName(t, policy))))
            .ToList();

    private static void Place(TreeNode start, string basePath, string relativeNamespace, TypeNode type, Dictionary<string, TreeNode> index)
    {
        var node = start;
        var path = basePath;
        foreach (var segment in NamePath.Segments(relativeNamespace))
        {
            path = NamePath.Join(path, segment);
            node = Child(node, "ns:" + path, segment, path, index);
        }
        node.Types.Add(type);
    }

    private static TreeNode Child(TreeNode parent, string id, string label, string path, Dictionary<string, TreeNode> index)
    {
        if (index.TryGetValue(id, out var existing)) return existing;
        var child = new TreeNode(id, label, NodeKind.Namespace, parent, path);
        parent.Children.Add(child);
        index[id] = child;
        return child;
    }

    private static void AddGroup(TreeNode parent, ProposalGroup group, int level, List<Assignment> entries, Dictionary<string, TreeNode> index)
    {
        var id = UniqueId("g:" + (group.Id.Length > 0 ? group.Id : group.Label), index);
        var node = new TreeNode(id, group.Label.Length > 0 ? group.Label : group.Id, NodeKind.Group, parent, null);
        parent.Children.Add(node);
        index[id] = node;
        foreach (var entry in group.Namespaces)
        {
            if (entry.Group is not null) AddGroup(node, entry.Group, level, entries, index);
            else if (entry.Namespace is { } ns && entries.All(e => e.Path != ns)) entries.Add(new Assignment(ns, node, level));
        }
    }

    private static string UniqueId(string id, Dictionary<string, TreeNode> index)
    {
        var candidate = id;
        for (var n = 2; index.ContainsKey(candidate); n++) candidate = $"{id}~{n}";
        return candidate;
    }

    private static int? Assign(TypeNode type, string prefix, List<Assignment> entries, TreeNode unassigned, Dictionary<string, TreeNode> index)
    {
        var name = NamePath.RelativeName(type, prefix);
        var relativeNamespace = NamePath.Relative(type.Namespace, prefix);
        var best = entries.Where(e => NamePath.Covers(e.Path, name)).MaxBy(e => e.Path.Length);
        if (best is null)
        {
            Place(unassigned, "", relativeNamespace, type, index);
            return null;
        }
        if (best.Path == name)
        {
            best.Group.Types.Add(type);
            return best.Level;
        }
        var entryNode = Child(best.Group, "ns:" + best.Path, best.Path.Length > 0 ? best.Path : "(all)", best.Path, index);
        var rest = relativeNamespace.Length > best.Path.Length
            ? relativeNamespace[(best.Path.Length == 0 ? 0 : best.Path.Length + 1)..]
            : "";
        Place(entryNode, best.Path, rest, type, index);
        return best.Level;
    }

    private static Dictionary<string, int?> RealLevels(IEnumerable<TypeNode> types, Policy policy, CodeModel model)
    {
        if (policy.Levels.Count == 0 && policy.Proposals.Count > 0)
        {
            var proposalTree = ForProposal(model, policy with { Proposals = [] }, policy.Proposals[0]);
            return types.ToDictionary(t => t.Id, t => proposalTree.LevelOfType(t.Id));
        }
        var levelOf = DependencyRule.LevelsFrom(policy.Levels);
        return types.ToDictionary(t => t.Id, t => levelOf(RelativeName(t, policy)));
    }

    private static void SortReal(TreeNode root, IReadOnlyList<string> order)
    {
        int Rank(TreeNode n)
        {
            var i = order.ToList().IndexOf(n.Label);
            return i < 0 ? int.MaxValue : i;
        }
        SortAlphabetically(root, keepGroupOrder: false);
        var sorted = root.Children.OrderBy(Rank).ThenBy(n => n.Label, StringComparer.Ordinal).ToList();
        root.Children.Clear();
        root.Children.AddRange(sorted);
    }

    private static void SortAlphabetically(TreeNode node, bool keepGroupOrder)
    {
        var sorted = node.Children
            .OrderBy(c => keepGroupOrder && c.Kind == NodeKind.Group ? 0 : 1)
            .ThenBy(c => keepGroupOrder && c.Kind == NodeKind.Group ? "" : c.Label, StringComparer.Ordinal)
            .ToList();
        node.Children.Clear();
        node.Children.AddRange(sorted);
        node.Types.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        foreach (var child in node.Children) SortAlphabetically(child, keepGroupOrder);
    }
}
