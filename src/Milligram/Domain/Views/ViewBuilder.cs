using Milligram.Domain.Hierarchy;
using Milligram.Domain.Metrics;
using Milligram.Domain.Model;
using Milligram.Domain.Policies;

namespace Milligram.Domain.Views;

/// <summary>
/// Builds one screen of the diagram: the children of the focused node, one level of their contents,
/// and whatever outside the focus they depend on (or that depends on them).
/// </summary>
public sealed class ViewBuilder
{
    public const string TypeIdPrefix = "t:";

    private readonly CodeModel model;
    private readonly Policy policy;
    private readonly MetricsSet metrics;
    private readonly DiagramTree tree;
    private readonly Dictionary<string, string> inside = [];
    private readonly Dictionary<string, string> outside = [];
    private readonly Dictionary<string, TreeNode> outsideNodes = [];
    private readonly Dictionary<string, Grades> gradeCache = [];
    private readonly List<ViewNode> nodes = [];

    private ViewBuilder(CodeModel model, Policy policy, MetricsSet metrics, DiagramTree tree)
    {
        this.model = model;
        this.policy = policy;
        this.metrics = metrics;
        this.tree = tree;
    }

    public static DiagramView Build(CodeModel model, Policy policy, MetricsSet metrics, DiagramTree tree, string? focusId) =>
        new ViewBuilder(model, policy, metrics, tree).Run(tree.Find(focusId));

    public static string NodeIdOf(TypeNode type) => TypeIdPrefix + type.Id;

    public static string StereotypeOf(TypeNode type) => type.Kind switch
    {
        TypeKind.Interface => "interface",
        TypeKind.Enum => "enum",
        TypeKind.Delegate => "delegate",
        _ when type.IsStatic => "static",
        _ when type.IsAbstract => "abstract",
        TypeKind.Record or TypeKind.RecordStruct => "record",
        TypeKind.Struct => "struct",
        _ => "class",
    };

    /// <summary>Members worth drawing on a class box: not private, not from nested types.</summary>
    public static IReadOnlyList<ViewMember> BoxMembers(TypeNode type) =>
        type.Members
            .Where(m => m.Visibility is not (Visibility.Private or Visibility.PrivateProtected))
            .Where(m => !m.Name.Contains('.') && m.Kind is not (MemberKind.Initializer or MemberKind.TopLevel))
            .Select(m => new ViewMember(m.Name, m.Signature, m.Kind, m.Visibility, m.IsStatic, m.IsAbstract))
            .ToList();

    private DiagramView Run(TreeNode focus)
    {
        AddInterior(focus);
        MapExterior(focus);
        var edges = BuildEdges();
        AddEndpointsOutside(edges);
        var breadcrumbs = focus.Ancestors().Reverse().Append(focus).Select(n => new NodeRef(n.Id, n.Label)).ToList();
        var context = new ContextInfo(tree.ContextId, tree.ContextName, tree.IsProposal);
        return new DiagramView(
            tree.Root.Label, context, new NodeRef(focus.Id, focus.Label), breadcrumbs,
            nodes, edges, nodes.Select(n => n.Level).Max());
    }

    private void AddInterior(TreeNode focus)
    {
        foreach (var component in focus.Children)
        {
            nodes.Add(Container(component, ViewNodeKind.Component, parent: null));
            foreach (var package in component.Children)
            {
                nodes.Add(Container(package, ViewNodeKind.Package, component.Id));
                foreach (var type in package.AllTypes()) inside[type.Id] = package.Id;
            }
            foreach (var type in component.Types) AddType(type, component.Id);
        }
        foreach (var type in focus.Types) AddType(type, parent: null);
    }

    private void AddType(TypeNode type, string? parent)
    {
        nodes.Add(TypeBox(type, ViewNodeKind.Type, parent));
        inside[type.Id] = NodeIdOf(type);
    }

    /// <summary>Everything outside the focus is represented by the sibling branch of the nearest shared ancestor.</summary>
    private void MapExterior(TreeNode focus)
    {
        for (TreeNode child = focus, ancestor = focus.Parent!; ancestor is not null; child = ancestor, ancestor = ancestor.Parent!)
        {
            foreach (var branch in ancestor.Children.Where(b => b != child))
            {
                outsideNodes[branch.Id] = branch;
                foreach (var type in branch.AllTypes()) outside[type.Id] = branch.Id;
            }
            foreach (var type in ancestor.Types) outside[type.Id] = NodeIdOf(type);
        }
    }

    private List<ViewEdge> BuildEdges()
    {
        var effective = EdgeRules.Apply(model.Edges, policy, NameOf);
        var foreign = model.Foreign.Select(f => f.Id).ToHashSet();
        var bundles = new Dictionary<(string, string), List<EdgePair>>();
        foreach (var edge in effective)
        {
            var from = AnchorOf(edge.From, foreign);
            var to = AnchorOf(edge.To, foreign);
            if (from is null || to is null || from == to) continue;
            if (!inside.ContainsKey(edge.From) && !inside.ContainsKey(edge.To)) continue;
            var violating = DependencyRule.IsViolating(edge.Kind, tree.LevelOfType(edge.From), tree.LevelOfType(edge.To));
            var pair = new EdgePair(NameOf(edge.From), NameOf(edge.To), edge.Kind, violating, edge.Count);
            if (!bundles.TryGetValue((from, to), out var pairs)) bundles[(from, to)] = pairs = [];
            pairs.Add(pair);
        }
        return bundles
            .Select(b => new ViewEdge(b.Key.Item1, b.Key.Item2, BundleKind(b.Value), b.Value.Any(p => p.Violating),
                b.Value.OrderBy(p => p.From, StringComparer.Ordinal).ThenBy(p => p.To, StringComparer.Ordinal).ToList()))
            .OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal)
            .ToList();
    }

    private static EdgeKind BundleKind(List<EdgePair> pairs) =>
        pairs.Select(p => p.Kind).Distinct().Count() == 1 ? pairs[0].Kind : EdgeKind.Dependency;

    private string? AnchorOf(string id, HashSet<string> foreign) =>
        inside.GetValueOrDefault(id) ?? outside.GetValueOrDefault(id) ?? (foreign.Contains(id) ? id : null);

    private string NameOf(string id) =>
        tree.Type(id) is { } type ? NamePath.RelativeName(type, policy.Prefix)
        : model.Foreign.FirstOrDefault(f => f.Id == id)?.Label ?? id;

    private void AddEndpointsOutside(List<ViewEdge> edges)
    {
        var shown = nodes.Select(n => n.Id).ToHashSet();
        foreach (var id in edges.SelectMany(e => new[] { e.From, e.To }).Where(shown.Add))
        {
            if (outsideNodes.TryGetValue(id, out var branch)) nodes.Add(Container(branch, ViewNodeKind.External, parent: null));
            else if (id.StartsWith(TypeIdPrefix, StringComparison.Ordinal) && tree.Type(id[TypeIdPrefix.Length..]) is { } type)
                nodes.Add(TypeBox(type, ViewNodeKind.External, parent: null));
            else if (model.Foreign.FirstOrDefault(f => f.Id == id) is { } lib)
                nodes.Add(new ViewNode { Id = lib.Id, Label = lib.Label, Kind = ViewNodeKind.Foreign, Target = lib.Label });
        }
    }

    private ViewNode Container(TreeNode node, ViewNodeKind kind, string? parent)
    {
        var label = kind == ViewNodeKind.External && node.Kind == NodeKind.Namespace ? node.Path ?? node.Label : node.Label;
        return new ViewNode
        {
            Id = node.Id,
            Label = label.Length > 0 ? label : node.Label,
            Kind = kind,
            Parent = parent,
            Level = tree.LevelOf(node),
            Drill = node.Id,
            Target = node.Path,
            IsGroup = node.Kind == NodeKind.Group,
            TypeCount = node.AllTypes().Count(),
            Contents = node.Children.Select(c => c.Label).ToList(),
            Grades = Grading.Worst(node.AllTypes().Select(GradeOf), policy.Thresholds.MissingIsWorst),
        };
    }

    private ViewNode TypeBox(TypeNode type, ViewNodeKind kind, string? parent) => new()
    {
        Id = NodeIdOf(type),
        Label = type.Name,
        Kind = kind,
        Parent = parent,
        Level = tree.LevelOfType(type.Id),
        Stereotype = StereotypeOf(type),
        TypeId = type.Id,
        Namespace = type.Namespace,
        Target = NamePath.RelativeName(type, policy.Prefix),
        TypeCount = 1,
        Members = kind == ViewNodeKind.Type ? BoxMembers(type) : [],
        Grades = GradeOf(type),
    };

    private Grades GradeOf(TypeNode type)
    {
        if (!gradeCache.TryGetValue(type.Id, out var grades))
            gradeCache[type.Id] = grades = Grading.ForType(type, metrics, policy.Thresholds);
        return grades;
    }
}
