using Milligram.Domain.Boards;
using Milligram.Domain.Metrics;
using Milligram.Domain.Model;
using Milligram.Domain.Policies;

namespace Milligram.Domain.Views;

/// <summary>
/// Turns a board into a view the viewer already knows how to draw. Boxes that refer to code keep the metric
/// colours of what they point at; a reference that no longer resolves becomes a missing box, not a failure.
/// </summary>
public static class BoardViewBuilder
{
    public const string ContextPrefix = "board:";
    public const string BoxIdPrefix = "b:";

    public static string ContextOf(string boardId) => ContextPrefix + boardId;

    /// <summary>The board id inside a "board:" context, or null for any other context.</summary>
    public static string? BoardIdOf(string? contextId) =>
        contextId is not null && contextId.StartsWith(ContextPrefix, StringComparison.Ordinal) ? contextId[ContextPrefix.Length..] : null;

    public static DiagramView Build(Board board, CodeModel model, Policy policy, MetricsSet metrics)
    {
        var resolver = new CodeReferences(model, policy.Prefix);
        var nodes = board.Boxes.Select(box => Node(box, resolver, policy, metrics)).ToList();
        var edges = board.Arrows
            .Select(arrow => new ViewEdge(BoxIdPrefix + arrow.From, BoxIdPrefix + arrow.To, EdgeKind.Dependency, false, []) { Label = Clean(arrow.Label) })
            .ToList();
        var title = board.Title.Length > 0 ? board.Title : board.IdOrSlug();
        var focus = new NodeRef(ContextOf(board.IdOrSlug()), title);
        return new DiagramView(title, new ContextInfo(focus.Id, title, false) { IsBoard = true }, focus, [focus], nodes, edges, null);
    }

    /// <summary>The references on a board that no code in the model answers to, in board order.</summary>
    public static IReadOnlyList<string> Unresolved(Board board, CodeModel model, string prefix)
    {
        var resolver = new CodeReferences(model, prefix);
        return board.Boxes.Where(b => Clean(b.Ref) is { } reference && resolver.Find(reference) is null).Select(b => b.Ref!.Trim()).ToList();
    }

    private static ViewNode Node(BoardBox box, CodeReferences resolver, Policy policy, MetricsSet metrics)
    {
        var id = BoxIdPrefix + box.Id;
        var label = Clean(box.Label);
        if (Clean(box.Note) is { } note) return new ViewNode { Id = id, Label = note, Kind = ViewNodeKind.Note };
        if (Clean(box.Ref) is not { } reference) return new ViewNode { Id = id, Label = label!, Kind = ViewNodeKind.Free };
        if (resolver.Find(reference) is not { } found)
            return new ViewNode { Id = id, Label = label ?? reference, Kind = ViewNodeKind.Missing, Caption = $"not in the code: {reference}" };
        var (type, member) = found;
        var span = member?.Span ?? type.Spans.FirstOrDefault();
        return new ViewNode
        {
            Id = id,
            Label = label ?? (member is null ? type.Name : $"{type.Name}.{member.Name}"),
            Kind = ViewNodeKind.Type,
            Stereotype = member is null ? ViewBuilder.StereotypeOf(type) : member.Kind.ToString().ToLowerInvariant(),
            TypeId = type.Id,
            Namespace = type.Namespace,
            Target = NamePath.RelativeName(type, policy.Prefix),
            TypeCount = 1,
            Caption = type.Namespace,
            File = span?.File,
            Line = span?.StartLine,
            Grades = member is null ? Grading.ForType(type, metrics, policy.Thresholds) : Grading.ForMember(member, metrics, policy.Thresholds),
        };
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>
    /// Accepts what an agent is likely to write: a type or member id from the model, either without the policy
    /// prefix, "Type.Member" without a parameter list, and the viewer's "t:" node ids.
    /// </summary>
    private sealed class CodeReferences
    {
        private readonly Dictionary<string, TypeNode> types = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (TypeNode, MemberNode)> members = new(StringComparer.Ordinal);
        private readonly string prefix;

        public CodeReferences(CodeModel model, string prefix)
        {
            this.prefix = prefix;
            foreach (var type in model.Types)
            {
                types.TryAdd(type.Id, type);
                foreach (var member in type.Members) members.TryAdd(member.Id, (type, member));
            }
        }

        public (TypeNode Type, MemberNode? Member)? Find(string reference)
        {
            if (reference.StartsWith(ViewBuilder.TypeIdPrefix, StringComparison.Ordinal)) reference = reference[ViewBuilder.TypeIdPrefix.Length..];
            foreach (var candidate in Candidates(reference))
            {
                if (types.TryGetValue(candidate, out var type)) return (type, null);
                if (members.TryGetValue(candidate, out var exact)) return exact;
                if (ByName(candidate) is { } named) return named;
            }
            return null;
        }

        /// <summary>The reference as written first, then relative to the prefix: "App.Box" may mean App.App.Box.</summary>
        private IEnumerable<string> Candidates(string reference)
        {
            yield return reference;
            if (prefix.Length > 0) yield return prefix + "." + reference;
        }

        /// <summary>"Shop.Orders.Handler.Run" or "…Run()" names a member without its parameter types; the first overload wins.</summary>
        private (TypeNode, MemberNode)? ByName(string reference)
        {
            var open = reference.IndexOf('(', StringComparison.Ordinal);
            var name = open < 0 ? reference : reference[..open];
            var dot = name.LastIndexOf('.');
            if (dot <= 0 || !types.TryGetValue(name[..dot], out var type)) return null;
            var member = type.Members.FirstOrDefault(m => string.Equals(m.Name, name[(dot + 1)..], StringComparison.Ordinal));
            return member is null ? null : (type, member);
        }
    }
}
