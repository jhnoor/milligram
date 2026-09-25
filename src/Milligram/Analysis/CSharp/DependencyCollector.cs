using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Milligram.Domain.Model;
using TypeKind = Microsoft.CodeAnalysis.TypeKind;
using Milligram.Domain.Policies;

namespace Milligram.Analysis.CSharp;

/// <summary>
/// Source dependencies between top-level types: every name a type mentions that binds to another
/// project type (or a listed foreign library). Base lists give inheritance and implementation.
/// </summary>
internal sealed class DependencyCollector(IReadOnlyDictionary<INamedTypeSymbol, TypeAccumulator> types, IReadOnlyList<string> foreignPrefixes)
{
    private readonly Dictionary<(string From, string To), (EdgeKind Kind, int Count)> edges = [];
    private readonly Dictionary<string, ForeignNode> foreign = [];

    public IReadOnlyList<DependencyEdge> Edges =>
        edges.Select(e => new DependencyEdge(e.Key.From, e.Key.To, e.Value.Kind, e.Value.Count))
            .OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ToList();

    public IReadOnlyList<ForeignNode> Foreign => foreign.Values.OrderBy(f => f.Id, StringComparer.Ordinal).ToList();

    public void Collect(TypeAccumulator type)
    {
        foreach (var (node, model) in type.Parts)
        {
            if (node is BaseTypeDeclarationSyntax { BaseList: { } baseList })
                foreach (var baseType in baseList.Types)
                    AddBase(type, model.GetTypeInfo(baseType.Type).Type);

            foreach (var name in node.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
            {
                if (name is IdentifierNameSyntax { IsVar: true }) continue;
                var info = model.GetSymbolInfo(name);
                Add(type.Id, Target(info.Symbol ?? info.CandidateSymbols.FirstOrDefault()), EdgeKind.Dependency);
            }
        }
    }

    private void AddBase(TypeAccumulator type, ITypeSymbol? baseType)
    {
        var implements = baseType?.TypeKind == TypeKind.Interface && type.Symbol.TypeKind != TypeKind.Interface;
        Add(type.Id, TargetOf(baseType), implements ? EdgeKind.Implements : EdgeKind.Inheritance);
    }

    private void Add(string from, string? to, EdgeKind kind)
    {
        if (to is null || to == from) return;
        var key = (from, to);
        edges[key] = edges.TryGetValue(key, out var existing)
            ? ((EdgeKind)Math.Max((int)existing.Kind, (int)kind), existing.Count + 1)
            : (kind, 1);
    }

    private string? Target(ISymbol? symbol) => symbol switch
    {
        IAliasSymbol alias => TargetOf(alias.Target as ITypeSymbol),
        ITypeSymbol type => TargetOf(type),
        IMethodSymbol { ReducedFrom: { } extension } => TargetOf(extension.ContainingType),
        IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol => TargetOf(symbol.ContainingType),
        _ => null,
    };

    private string? TargetOf(ITypeSymbol? type)
    {
        while (true)
        {
            if (type is IArrayTypeSymbol array) type = array.ElementType;
            else if (type is IPointerTypeSymbol pointer) type = pointer.PointedAtType;
            else break;
        }
        if (type is not INamedTypeSymbol { TypeKind: not TypeKind.Error } named) return null;
        var outer = SymbolNames.Outermost(named.OriginalDefinition);
        if (types.TryGetValue(outer, out var internalType)) return internalType.Id;
        return ForeignOf(outer);
    }

    private string? ForeignOf(INamedTypeSymbol type)
    {
        if (foreignPrefixes.Count == 0 || type.Locations.Any(l => l.IsInSource)) return null;
        var ns = SymbolNames.Namespace(type);
        var name = NamePath.Join(ns, type.Name);
        var match = foreignPrefixes.Where(p => p.Length > 0 && NamePath.Covers(p, name)).MaxBy(p => p.Length);
        if (match is null) return null;
        var id = CodeModel.ForeignIdPrefix + match;
        foreign.TryAdd(id, new ForeignNode(id, match));
        return id;
    }
}
