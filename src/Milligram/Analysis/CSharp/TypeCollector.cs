using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Milligram.Domain.Model;

namespace Milligram.Analysis.CSharp;

/// <summary>Everything found about one top-level type across its partial declarations; nested types fold in.</summary>
internal sealed class TypeAccumulator(INamedTypeSymbol symbol, string root)
{
    private readonly List<SourceSpan> spans = [];
    private readonly Dictionary<string, MemberNode> members = [];

    public INamedTypeSymbol Symbol { get; } = symbol;
    public string Id { get; } = SymbolNames.TypeId(symbol);

    /// <summary>Syntax to walk for dependencies, with the semantic model that binds it.</summary>
    public List<(SyntaxNode Node, SemanticModel Model)> Parts { get; } = [];

    public void AddPart(SyntaxNode node, SemanticModel model)
    {
        Parts.Add((node, model));
        spans.Add(MemberReader.SpanOf(node, root));
    }

    public void AddMembers(MemberDeclarationSyntax declaration, INamedTypeSymbol declared, SemanticModel model)
    {
        var reader = new MemberReader(model, root, Id, NestedPrefix(declared), declared.Name);
        if (declaration is TypeDeclarationSyntax type && reader.PrimaryConstructor(type) is { } primary) Add(primary);
        var list = declaration switch
        {
            TypeDeclarationSyntax t => t.Members.Where(m => m is not (BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)),
            EnumDeclarationSyntax e => e.Members.Cast<MemberDeclarationSyntax>(),
            _ => [],
        };
        foreach (var member in list.SelectMany(reader.Read)) Add(member);
    }

    public void AddTopLevelStatements(CompilationUnitSyntax unit, SemanticModel model)
    {
        var statements = unit.Members.OfType<GlobalStatementSyntax>().ToList();
        var span = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(statements[0].SpanStart, statements[^1].Span.End);
        var sourceSpan = MemberReader.SpanOf(unit.SyntaxTree, span, root);
        spans.Add(sourceSpan);
        Parts.AddRange(statements.Select(s => ((SyntaxNode)s, model)));
        Add(new MemberNode(Id + ".<Main>$", "(top-level statements)", MemberKind.TopLevel, "top-level statements",
            Visibility.Private, true, false, sourceSpan, Complexity.Of(statements),
            MemberReader.Hash(string.Concat(statements.Select(s => s.ToString())))));
    }

    public TypeNode ToNode() => new(
        Id,
        SymbolNames.TypeName(Symbol),
        SymbolNames.Namespace(Symbol),
        KindOf(Symbol),
        SymbolNames.Visibility(Symbol.DeclaredAccessibility),
        Symbol.IsAbstract && Symbol.TypeKind != Microsoft.CodeAnalysis.TypeKind.Interface,
        Symbol.IsStatic,
        spans.OrderBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.Start).ToList(),
        members.Values.ToList());

    /// <summary>Partial methods appear twice; keep the part with a body.</summary>
    private void Add(MemberNode member)
    {
        if (members.TryGetValue(member.Id, out var existing) && existing.Complexity is not null) return;
        members[member.Id] = member;
    }

    private string NestedPrefix(INamedTypeSymbol declared)
    {
        var names = new List<string>();
        for (var t = declared; t is not null && !SymbolEqualityComparer.Default.Equals(t, Symbol); t = t.ContainingType) names.Add(t.Name);
        names.Reverse();
        return names.Count == 0 ? "" : string.Join('.', names) + ".";
    }

    private static Domain.Model.TypeKind KindOf(INamedTypeSymbol symbol) => symbol.TypeKind switch
    {
        Microsoft.CodeAnalysis.TypeKind.Interface => Domain.Model.TypeKind.Interface,
        Microsoft.CodeAnalysis.TypeKind.Enum => Domain.Model.TypeKind.Enum,
        Microsoft.CodeAnalysis.TypeKind.Delegate => Domain.Model.TypeKind.Delegate,
        Microsoft.CodeAnalysis.TypeKind.Struct => symbol.IsRecord ? Domain.Model.TypeKind.RecordStruct : Domain.Model.TypeKind.Struct,
        _ => symbol.IsRecord ? Domain.Model.TypeKind.Record : Domain.Model.TypeKind.Class,
    };
}

/// <summary>Finds every type declaration (and top-level program) in the compilation's trees.</summary>
internal sealed class TypeCollector(CSharpCompilation compilation, string root)
{
    private readonly Dictionary<INamedTypeSymbol, TypeAccumulator> types = new(SymbolEqualityComparer.Default);

    public IReadOnlyDictionary<INamedTypeSymbol, TypeAccumulator> Collect(IEnumerable<SyntaxTree> trees)
    {
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            var unit = tree.GetCompilationUnitRoot();
            foreach (var declaration in Declarations(unit.Members))
            {
                if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol declared) continue;
                var outer = SymbolNames.Outermost(declared);
                var accumulator = Get(outer);
                if (SymbolEqualityComparer.Default.Equals(declared, outer)) accumulator.AddPart(declaration, model);
                accumulator.AddMembers(declaration, declared, model);
            }
            if (unit.Members.OfType<GlobalStatementSyntax>().Any() && model.GetDeclaredSymbol(unit) is { } entry)
                Get(entry.ContainingType).AddTopLevelStatements(unit, model);
        }
        return types;
    }

    private TypeAccumulator Get(INamedTypeSymbol symbol)
    {
        if (!types.TryGetValue(symbol, out var accumulator)) types[symbol] = accumulator = new TypeAccumulator(symbol, root);
        return accumulator;
    }

    private static IEnumerable<MemberDeclarationSyntax> Declarations(IEnumerable<MemberDeclarationSyntax> members)
    {
        foreach (var member in members)
        {
            switch (member)
            {
                case BaseNamespaceDeclarationSyntax ns:
                    foreach (var inner in Declarations(ns.Members)) yield return inner;
                    break;
                case TypeDeclarationSyntax type:
                    yield return type;
                    foreach (var inner in Declarations(type.Members)) yield return inner;
                    break;
                case EnumDeclarationSyntax or DelegateDeclarationSyntax:
                    yield return member;
                    break;
            }
        }
    }
}
