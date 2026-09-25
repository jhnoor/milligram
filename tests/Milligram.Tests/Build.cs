using Milligram.Domain.Model;
using Milligram.Domain.Policies;

namespace Milligram.Tests;

/// <summary>Terse builders for hand-made code models.</summary>
internal static class Build
{
    public static SourceSpan Span(string file, int startLine, int endLine, int start = 0, int end = 0) =>
        new(file, startLine, 1, endLine, 80, start, end);

    public static MemberNode Member(string typeId, string name, int? complexity = 1, string file = "a.cs",
        int startLine = 1, int endLine = 1, string hash = "h", Visibility visibility = Visibility.Public) =>
        new($"{typeId}.{name}()", name, MemberKind.Method, $"{name}(): void", visibility, false, false,
            Span(file, startLine, endLine), complexity, hash);

    public static TypeNode Type(string id, params MemberNode[] members) => Type(id, TypeKind.Class, members);

    public static TypeNode Type(string id, TypeKind kind, params MemberNode[] members)
    {
        var dot = id.LastIndexOf('.');
        var ns = dot < 0 ? "" : id[..dot];
        var name = dot < 0 ? id : id[(dot + 1)..];
        var file = members.Length > 0 ? members[0].Span.File : $"{name}.cs";
        return new TypeNode(id, name, ns, kind, Visibility.Public, false, false, [Span(file, 1, 100)], members);
    }

    public static DependencyEdge Edge(string from, string to, EdgeKind kind = EdgeKind.Dependency, int count = 1) =>
        new(from, to, kind, count);

    public static CodeModel Model(IEnumerable<TypeNode> types, IEnumerable<DependencyEdge>? edges = null, IEnumerable<ForeignNode>? foreign = null) =>
        new("Test", "App", DateTimeOffset.UnixEpoch, types.ToList(), (foreign ?? []).ToList(), (edges ?? []).ToList());

    public static Policy Policy(params string[][] levels) =>
        new() { Prefix = "App", Levels = levels.Select(l => (IReadOnlyList<string>)l.ToList()).ToList() };

    /// <summary>A small layered app: Web -> Services -> Domain, plus one violation Domain -> Web.</summary>
    public static CodeModel LayeredApp() => Model(
        [
            Type("App.Domain.Order"),
            Type("App.Domain.Rules.Pricing"),
            Type("App.Services.Checkout"),
            Type("App.Services.IRepository", TypeKind.Interface),
            Type("App.Web.Controller"),
            Type("App.Web.Sql.SqlRepository"),
            Type("App.Program"),
        ],
        [
            Edge("App.Web.Controller", "App.Services.Checkout"),
            Edge("App.Services.Checkout", "App.Domain.Order", count: 3),
            Edge("App.Services.Checkout", "App.Domain.Rules.Pricing"),
            Edge("App.Web.Sql.SqlRepository", "App.Services.IRepository", EdgeKind.Implements),
            Edge("App.Domain.Order", "App.Web.Controller"),
            Edge("App.Program", "App.Web.Controller"),
            Edge("App.Web.Controller", "x:Microsoft.AspNetCore"),
        ],
        [new ForeignNode("x:Microsoft.AspNetCore", "Microsoft.AspNetCore")]);

    public static Policy LayeredPolicy() => Policy(["Domain"], ["Services"], ["Web"]);
}
