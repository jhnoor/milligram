using Milligram.Domain.Model;

namespace Milligram.Domain.Hierarchy;

public enum NodeKind { Root, Namespace, Group }

/// <summary>A box in the component tree: the root, a namespace, or a proposal group.</summary>
public sealed class TreeNode(string id, string label, NodeKind kind, TreeNode? parent, string? path)
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public NodeKind Kind { get; } = kind;
    public TreeNode? Parent { get; } = parent;

    /// <summary>Relative namespace path (namespace nodes) or null (root and groups).</summary>
    public string? Path { get; } = path;

    public List<TreeNode> Children { get; } = [];
    public List<TypeNode> Types { get; } = [];

    public IEnumerable<TypeNode> AllTypes() => Types.Concat(Children.SelectMany(c => c.AllTypes()));

    public IEnumerable<TreeNode> Ancestors()
    {
        for (var node = Parent; node is not null; node = node.Parent) yield return node;
    }

    public IEnumerable<TreeNode> SelfAndDescendants() => Children.SelectMany(c => c.SelfAndDescendants()).Prepend(this);

    public bool IsEmpty => Types.Count == 0 && Children.Count == 0;

    public override string ToString() => Id;
}

/// <summary>The component tree for one diagram context: the real namespace tree or a proposal.</summary>
public sealed class DiagramTree
{
    public const string RealContext = "real";

    private readonly Dictionary<string, TreeNode> nodes;
    private readonly Dictionary<string, TypeNode> types;
    private readonly Dictionary<string, int?> typeLevels;
    private readonly Dictionary<string, int?> nodeLevels = [];

    public DiagramTree(string contextId, string contextName, TreeNode root, IReadOnlyDictionary<string, int?> typeLevels)
    {
        ContextId = contextId;
        ContextName = contextName;
        Root = root;
        nodes = root.SelfAndDescendants().ToDictionary(n => n.Id);
        types = root.AllTypes().ToDictionary(t => t.Id);
        this.typeLevels = new Dictionary<string, int?>(typeLevels);
    }

    public string ContextId { get; }
    public string ContextName { get; }
    public bool IsProposal => ContextId != RealContext;
    public TreeNode Root { get; }

    public TreeNode Find(string? id) => id is not null && nodes.TryGetValue(id, out var node) ? node : Root;

    public TreeNode? TryFind(string id) => nodes.GetValueOrDefault(id);

    public bool Contains(string typeId) => types.ContainsKey(typeId);

    public TypeNode? Type(string typeId) => types.GetValueOrDefault(typeId);

    public IEnumerable<TypeNode> Types => types.Values;

    public int? LevelOfType(string typeId) => typeLevels.GetValueOrDefault(typeId);

    /// <summary>A component takes the maximum (outermost) level of its elements.</summary>
    public int? LevelOf(TreeNode node)
    {
        if (nodeLevels.TryGetValue(node.Id, out var cached)) return cached;
        var level = node.AllTypes().Select(t => LevelOfType(t.Id)).Max();
        nodeLevels[node.Id] = level;
        return level;
    }
}
