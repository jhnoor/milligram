using Microsoft.CodeAnalysis;

namespace Milligram.Analysis.CSharp;

/// <summary>Stable, readable names for symbols: ids for matching metrics, signatures for display.</summary>
public static class SymbolNames
{
    private static readonly SymbolDisplayFormat QualifiedFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat ShortFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                              SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly SymbolDisplayFormat IdTypeFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static string TypeId(INamedTypeSymbol type) => type.ToDisplayString(QualifiedFormat);

    public static string TypeName(INamedTypeSymbol type) => type.ToDisplayString(ShortFormat);

    public static string Namespace(INamedTypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "";

    public static string Short(ITypeSymbol type) => type.ToDisplayString(ShortFormat);

    public static string ParameterIds(IEnumerable<IParameterSymbol> parameters) =>
        string.Join(",", parameters.Select(p => RefPrefix(p) + p.Type.ToDisplayString(IdTypeFormat)));

    public static string ParameterList(IEnumerable<IParameterSymbol> parameters) =>
        string.Join(", ", parameters.Select(p => $"{RefPrefix(p)}{Short(p.Type)} {p.Name}"));

    public static string TypeParameters(IMethodSymbol method) =>
        method.TypeParameters.Length == 0 ? "" : "<" + string.Join(", ", method.TypeParameters.Select(t => t.Name)) + ">";

    private static string RefPrefix(IParameterSymbol parameter) => parameter.RefKind switch
    {
        RefKind.Ref => "ref ",
        RefKind.Out => "out ",
        RefKind.In => "in ",
        RefKind.RefReadOnlyParameter => "ref readonly ",
        _ => parameter.IsParams ? "params " : "",
    };

    public static Domain.Model.Visibility Visibility(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => Domain.Model.Visibility.Public,
        Accessibility.Internal => Domain.Model.Visibility.Internal,
        Accessibility.Protected => Domain.Model.Visibility.Protected,
        Accessibility.ProtectedOrInternal => Domain.Model.Visibility.ProtectedInternal,
        Accessibility.ProtectedAndInternal => Domain.Model.Visibility.PrivateProtected,
        _ => Domain.Model.Visibility.Private,
    };

    /// <summary>The outermost containing type: nested types are folded into it.</summary>
    public static INamedTypeSymbol Outermost(INamedTypeSymbol type)
    {
        while (type.ContainingType is not null) type = type.ContainingType;
        return type;
    }
}
