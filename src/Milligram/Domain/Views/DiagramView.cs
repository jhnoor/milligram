using Milligram.Domain.Metrics;
using Milligram.Domain.Model;

namespace Milligram.Domain.Views;

public sealed record ContextInfo(string Id, string Name, bool IsProposal);

public sealed record NodeRef(string Id, string Label);

public enum ViewNodeKind { Component, Package, Type, External, Foreign }

/// <summary>One box on screen. <see cref="Parent"/> is the containing component, if any.</summary>
public sealed record ViewNode
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required ViewNodeKind Kind { get; init; }
    public string? Parent { get; init; }
    public int? Level { get; init; }
    public string? Stereotype { get; init; }

    /// <summary>Tree node id to focus on double-click, for containers and externals.</summary>
    public string? Drill { get; init; }

    public string? TypeId { get; init; }
    public string? Namespace { get; init; }

    /// <summary>Relative namespace path or type name: what Omit and Refresh act on.</summary>
    public string? Target { get; init; }

    public bool IsGroup { get; init; }
    public int TypeCount { get; init; }
    public IReadOnlyList<string> Contents { get; init; } = [];
    public IReadOnlyList<ViewMember> Members { get; init; } = [];
    public Grades Grades { get; init; } = Grades.Unknown;
}

public sealed record ViewMember(string Name, string Signature, MemberKind Kind, Visibility Visibility, bool IsStatic, bool IsAbstract);

/// <summary>An arrow between two visible nodes, bundling every type-level dependency it stands for.</summary>
public sealed record ViewEdge(string From, string To, EdgeKind Kind, bool Violating, IReadOnlyList<EdgePair> Pairs);

public sealed record EdgePair(string From, string To, EdgeKind Kind, bool Violating, int Count);

public sealed record DiagramView(
    string Title,
    ContextInfo Context,
    NodeRef Focus,
    IReadOnlyList<NodeRef> Breadcrumbs,
    IReadOnlyList<ViewNode> Nodes,
    IReadOnlyList<ViewEdge> Edges,
    int? MaxLevel);

public sealed record CardMember(
    string Id,
    string Name,
    string Signature,
    MemberKind Kind,
    Visibility Visibility,
    bool IsStatic,
    bool IsAbstract,
    string File,
    int Line,
    int EndLine,
    int? Complexity,
    CrapEntry? Crap,
    bool CrapStale,
    MutationEntry? Mutation,
    bool MutationStale);

public sealed record CardDependency(string Id, string Label, EdgeKind Kind, bool Violating, int Count, bool IsForeign);

public sealed record TypeCard(
    string Id,
    string Name,
    string Namespace,
    string RelativeName,
    TypeKind Kind,
    string Stereotype,
    Visibility Visibility,
    int? Level,
    IReadOnlyList<SourceSpan> Spans,
    CrapSummary? Crap,
    MutationSummary? Mutation,
    bool MutationTested,
    Grades Grades,
    IReadOnlyList<CardMember> Members,
    IReadOnlyList<CardDependency> DependsOn,
    IReadOnlyList<CardDependency> UsedBy);
