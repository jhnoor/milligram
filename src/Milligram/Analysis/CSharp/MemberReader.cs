using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Milligram.Domain.Model;

namespace Milligram.Analysis.CSharp;

/// <summary>Turns member declarations into <see cref="MemberNode"/>s with ids, signatures, spans, and complexity.</summary>
internal sealed class MemberReader(SemanticModel model, string root, string typeId, string namePrefix, string typeName)
{
    public IEnumerable<MemberNode> Read(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => One(Method(method)),
        ConstructorDeclarationSyntax constructor => One(Constructor(constructor)),
        DestructorDeclarationSyntax destructor => One(Destructor(destructor)),
        PropertyDeclarationSyntax property => One(Property(property)),
        IndexerDeclarationSyntax indexer => One(Indexer(indexer)),
        EventDeclarationSyntax @event => One(Event(@event)),
        EventFieldDeclarationSyntax events => Variables(events.Declaration, MemberKind.Event),
        FieldDeclarationSyntax fields => Variables(fields.Declaration, MemberKind.Field),
        OperatorDeclarationSyntax op => One(Operator(op)),
        ConversionOperatorDeclarationSyntax conversion => One(Conversion(conversion)),
        EnumMemberDeclarationSyntax value => One(EnumValue(value)),
        _ => [],
    };

    public MemberNode? PrimaryConstructor(TypeDeclarationSyntax type)
    {
        if (type.ParameterList is null) return null;
        var parameters = type.ParameterList.Parameters.Select(p => model.GetDeclaredSymbol(p)).OfType<IParameterSymbol>().ToList();
        return Make($"{typeId}.{namePrefix}.ctor({SymbolNames.ParameterIds(parameters)})", namePrefix + typeName, MemberKind.Constructor,
            $"{typeName}({SymbolNames.ParameterList(parameters)})", Visibility.Public, false, false, type.ParameterList, complexity: null);
    }

    public static SourceSpan SpanOf(SyntaxNode node, string root) => SpanOf(node.SyntaxTree, node.Span, root);

    public static SourceSpan SpanOf(SyntaxTree tree, Microsoft.CodeAnalysis.Text.TextSpan span, string root)
    {
        var lines = tree.GetLineSpan(span);
        return new SourceSpan(
            SourceFiles.Relative(root, tree.FilePath),
            lines.StartLinePosition.Line + 1, lines.StartLinePosition.Character + 1,
            lines.EndLinePosition.Line + 1, lines.EndLinePosition.Character,
            span.Start, span.End);
    }

    public static string Hash(string text) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    private static IEnumerable<MemberNode> One(MemberNode? node) => node is null ? [] : [node];

    private MemberNode? Method(MethodDeclarationSyntax syntax)
    {
        if (model.GetDeclaredSymbol(syntax) is not { } symbol) return null;
        var name = syntax.Identifier.Text + SymbolNames.TypeParameters(symbol);
        return Make(
            $"{typeId}.{namePrefix}{name}({SymbolNames.ParameterIds(symbol.Parameters)})",
            namePrefix + name, MemberKind.Method,
            $"{name}({SymbolNames.ParameterList(symbol.Parameters)}): {SymbolNames.Short(symbol.ReturnType)}",
            symbol, syntax, HasCode(syntax.Body, syntax.ExpressionBody) ? Complexity.Of(syntax) : null);
    }

    private MemberNode? Constructor(ConstructorDeclarationSyntax syntax)
    {
        if (model.GetDeclaredSymbol(syntax) is not { } symbol) return null;
        var hasCode = HasCode(syntax.Body, syntax.ExpressionBody) || syntax.Initializer is not null;
        return Make(
            $"{typeId}.{namePrefix}{(symbol.IsStatic ? ".cctor" : ".ctor")}({SymbolNames.ParameterIds(symbol.Parameters)})",
            namePrefix + (symbol.IsStatic ? "static " : "") + typeName, MemberKind.Constructor,
            $"{typeName}({SymbolNames.ParameterList(symbol.Parameters)})",
            symbol, syntax, hasCode ? Complexity.Of(syntax) : null);
    }

    private MemberNode? Destructor(DestructorDeclarationSyntax syntax)
    {
        if (model.GetDeclaredSymbol(syntax) is not { } symbol) return null;
        return Make($"{typeId}.{namePrefix}Finalize()", namePrefix + "~" + typeName, MemberKind.Destructor, $"~{typeName}()",
            symbol, syntax, HasCode(syntax.Body, syntax.ExpressionBody) ? Complexity.Of(syntax) : null);
    }

    private MemberNode? Property(PropertyDeclarationSyntax syntax)
    {
        if (model.GetDeclaredSymbol(syntax) is not { } symbol) return null;
        var name = syntax.Identifier.Text;
        return Make($"{typeId}.{namePrefix}{name}", namePrefix + name, MemberKind.Property,
            $"{name}: {SymbolNames.Short(symbol.Type)}", symbol, syntax,
            HasCode(syntax.ExpressionBody, syntax.AccessorList) ? Complexity.Of(syntax) : null);
    }

    private MemberNode? Indexer(IndexerDeclarationSyntax syntax)
    {
        if (model.GetDeclaredSymbol(syntax) is not { } symbol) return null;
        return Make($"{typeId}.{namePrefix}this[{SymbolNames.ParameterIds(symbol.Parameters)}]", namePrefix + "this[]", MemberKind.Indexer,
            $"this[{SymbolNames.ParameterList(symbol.Parameters)}]: {SymbolNames.Short(symbol.Type)}", symbol, syntax,
            HasCode(syntax.ExpressionBody, syntax.AccessorList) ? Complexity.Of(syntax) : null);
    }

    private MemberNode? Event(EventDeclarationSyntax syntax)
    {
        if (model.GetDeclaredSymbol(syntax) is not { } symbol) return null;
        var name = syntax.Identifier.Text;
        return Make($"{typeId}.{namePrefix}{name}", namePrefix + name, MemberKind.Event,
            $"{name}: {SymbolNames.Short(symbol.Type)}", symbol, syntax,
            HasCode(null, syntax.AccessorList) ? Complexity.Of(syntax) : null);
    }

    private IEnumerable<MemberNode> Variables(VariableDeclarationSyntax declaration, MemberKind kind)
    {
        foreach (var variable in declaration.Variables)
        {
            var symbol = model.GetDeclaredSymbol(variable);
            var type = symbol switch { IFieldSymbol f => f.Type, IEventSymbol e => e.Type, _ => null };
            if (symbol is null || type is null) continue;
            var name = variable.Identifier.Text;
            yield return Make($"{typeId}.{namePrefix}{name}", namePrefix + name, kind,
                $"{name}: {SymbolNames.Short(type)}", symbol, variable, complexity: null);
        }
    }

    private MemberNode? Operator(OperatorDeclarationSyntax syntax)
    {
        if (model.GetDeclaredSymbol(syntax) is not { } symbol) return null;
        var name = "operator " + syntax.OperatorToken.Text;
        return Make($"{typeId}.{namePrefix}{symbol.Name}({SymbolNames.ParameterIds(symbol.Parameters)})", namePrefix + name, MemberKind.Operator,
            $"{name}({SymbolNames.ParameterList(symbol.Parameters)}): {SymbolNames.Short(symbol.ReturnType)}", symbol, syntax,
            HasCode(syntax.Body, syntax.ExpressionBody) ? Complexity.Of(syntax) : null);
    }

    private MemberNode? Conversion(ConversionOperatorDeclarationSyntax syntax)
    {
        if (model.GetDeclaredSymbol(syntax) is not { } symbol) return null;
        var target = SymbolNames.Short(symbol.ReturnType);
        var name = $"{syntax.ImplicitOrExplicitKeyword.Text} operator {target}";
        return Make($"{typeId}.{namePrefix}{symbol.Name}({SymbolNames.ParameterIds(symbol.Parameters)}):{target}", namePrefix + name,
            MemberKind.Operator, $"{name}({SymbolNames.ParameterList(symbol.Parameters)})", symbol, syntax,
            HasCode(syntax.Body, syntax.ExpressionBody) ? Complexity.Of(syntax) : null);
    }

    private MemberNode? EnumValue(EnumMemberDeclarationSyntax syntax)
    {
        if (model.GetDeclaredSymbol(syntax) is not { } symbol) return null;
        var name = syntax.Identifier.Text;
        var signature = syntax.EqualsValue is { } value ? $"{name} = {value.Value}" : name;
        return Make($"{typeId}.{name}", name, MemberKind.EnumValue, signature, symbol, syntax, complexity: null);
    }

    private static bool HasCode(SyntaxNode? body, SyntaxNode? other) =>
        body is not null || other switch
        {
            AccessorListSyntax accessors => accessors.Accessors.Any(a => a.Body is not null || a.ExpressionBody is not null),
            null => false,
            _ => true,
        };

    private MemberNode Make(string id, string name, MemberKind kind, string signature, ISymbol symbol, SyntaxNode node, int? complexity) =>
        Make(id, name, kind, signature, SymbolNames.Visibility(symbol.DeclaredAccessibility), symbol.IsStatic,
            symbol.IsAbstract, node, complexity);

    /// <summary>Members of nested types keep their type path in the signature: "Cache.Clear(): void".</summary>
    private MemberNode Make(string id, string name, MemberKind kind, string signature, Visibility visibility,
        bool isStatic, bool isAbstract, SyntaxNode node, int? complexity) =>
        new(id, name, kind, namePrefix + signature, visibility, isStatic, isAbstract, SpanOf(node, root), complexity, Hash(node.ToString()));
}
